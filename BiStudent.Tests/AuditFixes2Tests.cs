using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BiStudent.Contracts;
using BiStudent.Server.Data;
using BiStudent.Server.Endpoints;
using BiStudent.Server.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BiStudent.Tests;

public sealed class AuditFixes2Tests : IDisposable
{
    private readonly string temporaryDirectory = Path.Combine(Path.GetTempPath(), $"BiStudent.Tests.{Guid.NewGuid():N}");
    private readonly WebApplicationFactory<Program> factory;
    private readonly HttpClient client;

    public AuditFixes2Tests()
    {
        Directory.CreateDirectory(temporaryDirectory);
        factory = new ApiFactory(temporaryDirectory);
        client = factory.CreateClient();
    }

    [Fact]
    public async Task CsvImportRollsBackGroupsAndSlots()
    {
        var token = await LoginAsync(client, "teacher", "teacher123");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        var csv = "day;start;end;subject;group;room;parity\n" +
            "пн;08:00;09:20;Атомарность CSV;Новая группа CSV;101;каждая\n" +
            "ошибка;10:00;11:20;Тест;Другая группа;102;каждая\n";
        using var response = await client.PostAsync("/api/schedule/import", new StringContent(csv, Encoding.UTF8, "text/csv"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, result.RootElement.GetProperty("added").GetInt32());
        Assert.True(result.RootElement.GetProperty("errors").GetArrayLength() > 0);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Groups.AnyAsync(x => x.Name == "Новая группа CSV"));
        Assert.False(await db.ScheduleSlots.AnyAsync(x => x.Subject == "Атомарность CSV"));
    }

    [Fact]
    public async Task FreeLoginStaysWithinLimitWhenBaseIsTaken()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var login = new string('a', 32);
        db.Teachers.Add(new Teacher { Login = login, FullName = "Занятый преподаватель" });
        await db.SaveChangesAsync();
        var import = scope.ServiceProvider.GetRequiredService<ScheduleImportService>();
        var candidate = await import.FreeLoginAsync(login);
        Assert.Equal(32, candidate.Length);
        Assert.EndsWith("2", candidate);
        Assert.NotEqual(login, candidate);
    }

    [Fact]
    public async Task OnlyIfUnmarkedKeepsExcusedMark()
    {
        using var test = new TestDb();
        var teacher = test.AddTeacher();
        var group = test.AddGroup("Группа", "Студент");
        var slot = test.AddSlot(teacher, group);
        var studentId = Assert.Single(group.Students).Id;
        var date = new DateOnly(2026, 9, 1);
        await test.Attendance.MarkAsync(slot.Id, date, studentId, AttendanceStatus.Excused, MarkMethod.Manual, ExcuseReason.Other);
        var changed = await test.Attendance.MarkAsync(slot.Id, date, studentId, AttendanceStatus.Present, MarkMethod.Manual, onlyIfUnmarked: true);
        Assert.False(changed);
        var mark = await test.Db.AttendanceMarks.AsNoTracking().SingleAsync();
        Assert.Equal(AttendanceStatus.Excused, mark.Status);
        Assert.Equal(ExcuseReason.Other, mark.ExcuseReason);
    }

    [Fact]
    public async Task SqliteConstraintBecomesConflict()
    {
        var result = await ApiEndpoints.HandleErrorsAsync(null!,
            _ => throw new DbUpdateException("x", new SqliteException("UNIQUE", 19)));
        Assert.Equal(StatusCodes.Status409Conflict, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    [Fact]
    public async Task DashboardExcludesOtherTeachersSubmissions()
    {
        using var test = new TestDb();
        var first = test.AddTeacher();
        var other = new Teacher { Login = "other", FullName = "Другой преподаватель" };
        test.Db.Teachers.Add(other);
        await test.Db.SaveChangesAsync();
        var group = test.AddGroup("Чужая группа", "Чужой студент");
        test.AddSlot(other, group);
        test.Db.FaceSubmissions.Add(new FaceSubmission {
            StudentId = Assert.Single(group.Students).Id, Status = FaceSubmissionStatus.Pending
        });
        await test.Db.SaveChangesAsync();
        var dashboard = await test.Stats.DashboardAsync(first.Id, DateTime.Now);
        Assert.Equal(0, dashboard.PendingFaceSubmissions);
    }

    [Fact]
    public async Task InvalidateReloadsRecognitionSettings()
    {
        var recognition = factory.Services.GetRequiredService<RecognitionService>();
        await recognition.SaveSettingsAsync(new RecognitionSettingsDto(0.45, 0.05, 3));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var frames = await db.AppSettings.SingleAsync(x => x.Key == "recognition.frames");
            frames.Value = "5";
            await db.SaveChangesAsync();
        }
        recognition.Invalidate();
        Assert.Equal(5, (await recognition.GetSettingsAsync()).FramesInRow);
    }

    private static async Task<TokenResponse> LoginAsync(HttpClient target, string login, string password)
    {
        using var response = await target.PostAsJsonAsync("/api/auth/login", new LoginRequest(login, password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
    }

    public void Dispose()
    {
        client.Dispose();
        factory.Dispose();
        Directory.Delete(temporaryDirectory, recursive: true);
    }

    private sealed class ApiFactory(string directory) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(ServerRoot());
            builder.UseSetting("ConnectionStrings:Default", $"Data Source={Path.Combine(directory, "test.db")};Pooling=False");
            builder.UseSetting("Demo:Enabled", "true");
        }

        private static string ServerRoot()
        {
            for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
            {
                var candidate = Path.Combine(current.FullName, "BiStudent.Server");
                if (File.Exists(Path.Combine(candidate, "seed", "students.demo.csv"))) return candidate;
            }
            throw new DirectoryNotFoundException("BiStudent.Server/seed/students.demo.csv");
        }
    }
}
