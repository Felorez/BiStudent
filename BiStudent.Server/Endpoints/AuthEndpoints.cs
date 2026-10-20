using System.Globalization;
using System.Security.Claims;
using BiStudent.Contracts;
using BiStudent.Server.Data;
using BiStudent.Server.Firebase;
using BiStudent.Server.Services;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BiStudent.Server.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuth(this WebApplication app)
    {
        var auth = app.MapGroup("/api/auth").AddEndpointFilter(ApiEndpoints.HandleErrorsAsync);
        auth.MapPost("/login", LoginAsync).AllowAnonymous().RequireRateLimiting("login");
        auth.MapPost("/refresh", RefreshAsync).AllowAnonymous().RequireRateLimiting("login");
        auth.MapPost("/change-password", ChangePasswordAsync).RequireAuthorization();

        app.MapGet("/api/me", MeAsync).RequireAuthorization().AddEndpointFilter(ApiEndpoints.HandleErrorsAsync);

        var teachers = app.MapGroup("/api/admin/teachers").RequireAuthorization("Admin").AddEndpointFilter(ApiEndpoints.HandleErrorsAsync);
        teachers.MapGet("", (AccountService accounts) => accounts.ListAsync());
        teachers.MapPost("", (SaveTeacherRequest request, AccountService accounts) => accounts.CreateAsync(request));
        teachers.MapPut("/{id:int}", (int id, SaveTeacherRequest request, ClaimsPrincipal user, AccountService accounts) =>
            accounts.UpdateAsync(id, request, ApiEndpoints.TeacherId(user)));
        teachers.MapPost("/{id:int}/reset-password", (int id, ClaimsPrincipal user, AccountService accounts) =>
            accounts.ResetPasswordAsync(id, ApiEndpoints.TeacherId(user)));
    }

    private static async Task<IResult> LoginAsync(LoginRequest request, AppDbContext db,
        IPasswordHasher<Teacher> hasher, SessionGeneration sessions, CloudAccountService cloud, CancellationToken ct)
    {
        if (cloud.Enabled)
        {
            var result = await cloud.SignInAsync(request.Login.Trim(), request.Password, ct);
            return result.Teacher is { } signedIn
                ? Results.SignIn(Principal(signedIn, sessions), authenticationScheme: BearerTokenDefaults.AuthenticationScheme)
                : Results.Json(new ErrorDto(result.Error ?? "Не удалось войти."), statusCode: result.Status);
        }
        var login = request.Login.Trim().ToLowerInvariant();
        var teacher = await db.Teachers.SingleOrDefaultAsync(x => x.Login.ToLower() == login);
        var verification = teacher is null ? PasswordVerificationResult.Failed
            : hasher.VerifyHashedPassword(teacher, teacher.PasswordHash, request.Password);
        if (teacher is null || verification == PasswordVerificationResult.Failed)
            return Results.Json(new ErrorDto("Неверный логин или пароль."), statusCode: StatusCodes.Status401Unauthorized);
        if (!teacher.IsActive)
            return Results.Json(new ErrorDto("Учётная запись отключена. Обратитесь к администратору."), statusCode: StatusCodes.Status403Forbidden);
        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
            teacher.PasswordHash = hasher.HashPassword(teacher, request.Password);
        teacher.LastLoginAt = DateTime.Now;
        await db.SaveChangesAsync();
        return Results.SignIn(Principal(teacher, sessions), authenticationScheme: BearerTokenDefaults.AuthenticationScheme);
    }

    private static async Task<IResult> RefreshAsync(RefreshRequest request, AppDbContext db,
        IOptionsMonitor<BearerTokenOptions> options, TimeProvider clock, SessionGeneration sessions)
    {
        var ticket = options.Get(BearerTokenDefaults.AuthenticationScheme).RefreshTokenProtector.Unprotect(request.RefreshToken);
        if (ticket is null || ticket.Properties.ExpiresUtc is not { } expires || expires <= clock.GetUtcNow() ||
            !sessions.IsCurrent(ticket.Principal))
            return Results.Json(new ErrorDto("Сессия завершена — войдите снова."), statusCode: StatusCodes.Status401Unauthorized);
        var id = ticket.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var stamp = ticket.Principal.FindFirstValue("bistudent:stamp");
        var teacher = int.TryParse(id, out var teacherId)
            ? await db.Teachers.SingleOrDefaultAsync(x => x.Id == teacherId) : null;
        if (teacher is null || !teacher.IsActive || teacher.SecurityStamp != stamp)
            return Results.Json(new ErrorDto("Сессия завершена — войдите снова."), statusCode: StatusCodes.Status401Unauthorized);
        return Results.SignIn(Principal(teacher, sessions), authenticationScheme: BearerTokenDefaults.AuthenticationScheme);
    }

    private static async Task<IResult> ChangePasswordAsync(ChangePasswordRequest request, ClaimsPrincipal user,
        AppDbContext db, IPasswordHasher<Teacher> hasher, SessionGeneration sessions, CloudAccountService cloud, CancellationToken ct)
    {
        var teacher = await db.Teachers.SingleAsync(x => x.Id == ApiEndpoints.TeacherId(user));
        // Привязанную к Firebase учётку проверяет Firebase: локальный хеш мог устареть после смены пароля на другом компьютере.
        var checkedByCloud = cloud.Enabled && teacher.FirebaseUid is not null;
        if (!checkedByCloud && hasher.VerifyHashedPassword(teacher, teacher.PasswordHash, request.CurrentPassword) == PasswordVerificationResult.Failed)
            throw new InvalidOperationException("Текущий пароль указан неверно.");
        if (request.NewPassword.Length < 8) throw new InvalidOperationException("Новый пароль должен содержать не меньше 8 символов.");
        if (request.NewPassword == request.CurrentPassword) throw new InvalidOperationException("Новый пароль должен отличаться от текущего.");
        if (string.Equals(request.NewPassword, teacher.Login, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Пароль не должен совпадать с логином.");
        if (cloud.Enabled) await cloud.ChangePasswordAsync(teacher, request.CurrentPassword, request.NewPassword, ct);
        teacher.PasswordHash = hasher.HashPassword(teacher, request.NewPassword);
        teacher.MustChangePassword = false;
        teacher.SecurityStamp = Guid.NewGuid().ToString("N");
        await db.SaveChangesAsync();
        return Results.SignIn(Principal(teacher, sessions), authenticationScheme: BearerTokenDefaults.AuthenticationScheme);
    }

    private static async Task<TeacherDto> MeAsync(ClaimsPrincipal user, AppDbContext db)
    {
        var teacher = await db.Teachers.AsNoTracking().SingleAsync(x => x.Id == ApiEndpoints.TeacherId(user));
        var pending = await db.FaceSubmissions.CountAsync(x => x.Status == FaceSubmissionStatus.Pending);
        return new TeacherDto(teacher.Id, teacher.FullName, teacher.Login, teacher.IsAdmin, teacher.MustChangePassword, pending);
    }

    private static ClaimsPrincipal Principal(Teacher teacher, SessionGeneration sessions)
    {
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, teacher.Id.ToString(CultureInfo.InvariantCulture)),
            new Claim(ClaimTypes.Name, teacher.FullName),
            new Claim("bistudent:login", teacher.Login),
            new Claim("bistudent:admin", teacher.IsAdmin ? "true" : "false"),
            new Claim("bistudent:stamp", teacher.SecurityStamp),
            sessions.Claim()
        ], BearerTokenDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }
}
