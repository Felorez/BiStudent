using System.Globalization;
using System.Text.Json.Serialization;
using BiStudent.Server.Data;
using BiStudent.Server.Endpoints;
using BiStudent.Server.Services;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddConsole();
var connection = builder.Configuration.GetConnectionString("Default") ?? "Data Source=App_Data/bistudent.db";
var sqlite = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connection);
if (!Path.IsPathRooted(sqlite.DataSource))
    sqlite.DataSource = Path.Combine(builder.Environment.ContentRootPath, sqlite.DataSource);
var data = Path.GetDirectoryName(Path.GetFullPath(sqlite.DataSource))!;
Directory.CreateDirectory(data);
Directory.CreateDirectory(Path.Combine(data, "keys"));

builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(sqlite.ToString()));
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>();
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(data, "keys")))
    .SetApplicationName("BiStudent");
builder.Services.AddAuthentication(BearerTokenDefaults.AuthenticationScheme)
    .AddBearerToken(options => options.BearerTokenExpiration = TimeSpan.FromHours(12));
builder.Services.AddAuthorization(options => options.AddPolicy("BackupAdmin", policy => policy.RequireAssertion(context =>
{
    var admins = builder.Configuration.GetSection("Admin:Logins").Get<string[]>() ?? ["teacher"];
    return context.User.FindFirst("bistudent:login")?.Value is { } login && admins.Contains(login, StringComparer.OrdinalIgnoreCase);
})));
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);
builder.Services.AddScoped<IPasswordHasher<Teacher>, PasswordHasher<Teacher>>();
builder.Services.AddScoped<ScheduleService>();
builder.Services.AddScoped<AttendanceService>();
builder.Services.AddScoped<StudentService>();
builder.Services.AddScoped<StatsService>();
builder.Services.AddScoped<ExportService>();
builder.Services.AddScoped<SeedService>();
builder.Services.AddSingleton<AttendanceNotifier>();
builder.Services.AddSingleton<RecognitionService>();
builder.Services.AddSingleton(new StoragePaths(Path.GetFullPath(sqlite.DataSource), sqlite.ToString()));
builder.Services.AddSingleton<SessionGeneration>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddHostedService<BackupService.AutoBackupWorker>();
builder.Services.AddScoped<FaceSampleHealth>();

var app = builder.Build();
var culture = CultureInfo.GetCultureInfo("ru-RU");
CultureInfo.DefaultThreadCurrentCulture = culture;
CultureInfo.DefaultThreadCurrentUICulture = culture;

app.UseExceptionHandler();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    // Операции с копиями сериализуются внутри BackupService; остальные запросы к API восстановление дожидается.
    var tracked = path.StartsWithSegments("/api") && !path.StartsWithSegments("/api/admin");
    var backups = context.RequestServices.GetRequiredService<BackupService>();
    if (tracked && !backups.TryEnterRequest())
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new BiStudent.Contracts.ErrorDto("Идёт восстановление резервной копии — повторите через минуту."));
        return;
    }
    try
    {
        if (context.User.Identity?.IsAuthenticated == true && !path.StartsWithSegments("/api/auth/login") &&
            !context.RequestServices.GetRequiredService<SessionGeneration>().IsCurrent(context.User))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new BiStudent.Contracts.ErrorDto("Сессия завершена — войдите снова."));
            return;
        }
        await next();
    }
    finally { if (tracked) backups.ExitRequest(); }
});
app.UseAuthorization();
app.MapHealthChecks("/health");
app.MapGet("/", () => Results.Text("BiStudent API. Клиент преподавателя — приложение BiStudent для Windows.", "text/plain; charset=utf-8"));
app.MapApi();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
    await scope.ServiceProvider.GetRequiredService<SeedService>().SeedAsync();
}

app.Run();

public partial class Program;
