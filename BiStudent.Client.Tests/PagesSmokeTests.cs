using System.Net;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using BiStudent.Contracts;

namespace BiStudent.Client.Tests;

// Все разделы главного окна открываются на фейковом сервере с данными и загружаются без ошибок.
[Collection("settings")]
public sealed class PagesSmokeTests
{
    private static readonly string[] PageKeys = ["today", "camera", "schedule", "journal", "groups", "report", "admin"];

    [Fact]
    public void EveryPageLoadsWithData()
    {
        var server = DemoServer();
        var visited = new List<string>();
        RunSta(() =>
        {
            var previousPath = ClientSettings.FilePath;
            var directory = Path.Combine(Path.GetTempPath(), "BiStudent.Tests", Guid.NewGuid().ToString("N"));
            ClientSettings.FilePath = Path.Combine(directory, "client.json");
            ExceptionDispatchInfo? failure = null;
            try
            {
                using var api = new ApiClient(new ClientSettings { ServerUrl = "http://test/" }, server);
                using var form = new MainForm(api)
                {
                    AutoOpenFirstPage = false, StartPosition = FormStartPosition.Manual,
                    Location = new Point(-4000, -4000), Size = new Size(1440, 900), ShowInTaskbar = false
                };
                form.Shown += async (_, _) =>
                {
                    try
                    {
                        await api.LoginAsync("teacher", "secret");
                        await api.LoadMeAsync();
                        foreach (var key in PageKeys)
                        {
                            await form.ShowPageAsync(key);
                            Assert.Null(form.LastPageError);
                            Assert.NotNull(form.CurrentPage);
                            using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
                            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.ClientSize));
                            visited.Add(key);
                        }
                    }
                    catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
                    finally { form.Close(); }
                };
                Application.Run(form);
            }
            finally
            {
                ClientSettings.FilePath = previousPath;
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
            failure?.Throw();
        });
        Assert.Equal(PageKeys, visited);
        var missing = server.Requests.Where(r => !server.Handles(r)).Select(r => $"{r.Method} {r.Path}").Distinct().ToList();
        Assert.True(missing.Count == 0, "Запросы без ответа фейкового сервера: " + string.Join("; ", missing));
    }

    internal static FakeServer DemoServer()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var now = DateTime.Now;
        var server = new FakeServer();
        var teacher = new TeacherDto(1, "Ахметова Алия Маратовна", "teacher", IsAdmin: true, PendingFaceSubmissions: 1);
        var slot = new SlotDto(10, 1, "23-ПО-103-4р-1", "Базы данных", "305", today.DayOfWeek,
            new TimeOnly(8, 30), new TimeOnly(9, 50), WeekParity.Every, new DateOnly(2026, 9, 1), null, null);
        var lesson = new LessonDto(slot, today, today.ToDateTime(slot.Start), today.ToDateTime(slot.End), false);
        var later = lesson with { Slot = slot with { Id = 11, Start = new TimeOnly(23, 0), End = new TimeOnly(23, 59) },
            StartsAt = today.ToDateTime(new TimeOnly(23, 0)), EndsAt = today.ToDateTime(new TimeOnly(23, 59)) };
        var dashboard = new DashboardDto(2, null, later, [lesson, later], 91.5, 88.2,
            [new GroupAttendanceDto(1, "23-ПО-103-4р-1", 90.1)],
            [new AtRiskStudentDto(101, "Абдиев Артур", "23-ПО-103-4р-1", 62.5, "Посещаемость ниже 70%")],
            40, 160, 4, 16, "Отметки камерой × 6 сек. + выгрузки журнала × 20 мин.",
            [new TrendPointDto(today.AddDays(-1), 90), new TrendPointDto(today, 92)], 78.4, 1);
        var students = new List<StudentDto>
        {
            new(101, "Абдиев Артур", 1, true),
            new(102, "Айсулатанов Руслан", 1, false, FacePending: true),
            new(103, "Ашимжанов Ильхам", 1, false, FaceUnreadable: true)
        };
        var attendance = new List<AttendanceRowDto>
        {
            new(101, "Абдиев Артур", AttendanceStatus.Present, MarkMethod.Camera, now, Score: 85),
            new(102, "Айсулатанов Руслан", AttendanceStatus.Excused, MarkMethod.Manual, now, ExcuseReason.Illness, "Справка", true),
            new(103, "Ашимжанов Ильхам", null, null, null)
        };
        var columns = new List<JournalColumnDto>
        {
            new(JournalColumnKind.Lesson, 10, today.AddDays(-7), new TimeOnly(8, 30), "Базы данных"),
            new(JournalColumnKind.Assessment, 5, today.AddDays(-3), null, "Базы данных", "Тест №1", AssessmentKind.Test)
        };
        var journal = new JournalDto(teacher.FullName, "23-ПО-103-4р-1", "Базы данных", today.AddDays(-30), today, 2, columns,
        [
            new(101, "Абдиев Артур", [new(AttendanceStatus.Present, null, null, 85), new(null, null, null, 90)], 1, 0, 0, 0, 0, 100, 87.5, 88),
            new(102, "Айсулатанов Руслан", [new(AttendanceStatus.Excused, ExcuseReason.Illness, "Справка", null), new(null, null, null, null)], 0, 0, 1, 2, 2, 0, null, null),
            new(103, "Ашимжанов Ильхам", [new(AttendanceStatus.Absent, null, null, null), new(null, null, null, 61)], 0, 1, 0, 2, 0, 0, 61, null)
        ]);
        var report = new ReportDto(dashboard, 6, 10, 1, 22, 6, 20);

        server.On(HttpMethod.Post, "api/auth/login", _ => FakeServer.Json(new TokenResponse("access", 3600, "refresh")));
        server.On(HttpMethod.Get, "api/me", _ => FakeServer.Json(teacher));
        server.On(HttpMethod.Get, "api/dashboard", _ => FakeServer.Json(dashboard));
        server.On(HttpMethod.Get, "api/report", _ => FakeServer.Json(report));
        server.On(HttpMethod.Get, "api/settings/automation", _ => FakeServer.Json(new AutomationSettingsDto(6, 20, 3)));
        server.On(HttpMethod.Get, "api/recognition/status", _ => FakeServer.Json(new EnrollResultDto(true, "Распознавание готово.")));
        server.On(HttpMethod.Get, "api/schedule/day", _ => FakeServer.Json(new List<LessonDto> { lesson, later }));
        server.On(HttpMethod.Get, "api/schedule/week", _ => FakeServer.Json(new List<LessonDto> { lesson, later }));
        server.On(HttpMethod.Get, "api/schedule/parity", _ => FakeServer.Json(WeekParity.Odd));
        server.On(HttpMethod.Get, "api/groups", _ => FakeServer.Json(new List<GroupDto> { new(1, "23-ПО-103-4р-1", 3, 1) }));
        server.On(HttpMethod.Get, "api/groups/1/students", _ => FakeServer.Json(students));
        server.On(HttpMethod.Get, "api/groups/1/enrollment-code", _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        server.On(HttpMethod.Get, "api/journal/1/subjects", _ => FakeServer.Json(new List<string> { "Базы данных" }));
        server.On(HttpMethod.Get, "api/journal/1", _ => FakeServer.Json(journal));
        server.On(HttpMethod.Get, "api/assessments", _ => FakeServer.Json(new List<AssessmentDto>
            { new(5, 1, "23-ПО-103-4р-1", "Базы данных", "Тест №1", AssessmentKind.Test, today.AddDays(-3)) }));
        server.On(HttpMethod.Get, $"api/lessons/10/{today:yyyy-MM-dd}/attendance", _ => FakeServer.Json(attendance));
        server.On(HttpMethod.Get, $"api/lessons/11/{today:yyyy-MM-dd}/attendance", _ => FakeServer.Json(attendance));
        server.On(HttpMethod.Get, "api/face-submissions", _ => FakeServer.Json(new List<FaceSubmissionDto>
            { new(7, 102, "Айсулатанов Руслан", 1, "23-ПО-103-4р-1", now, FaceSubmissionStatus.Pending, 3, false, null, 0, null) }));
        server.On(HttpMethod.Get, "api/admin/teachers", _ => FakeServer.Json(new List<TeacherAccountDto>
        {
            new(1, "teacher", teacher.FullName, true, true, false, now.AddDays(-30), now),
            new(2, "niyazova", "Ниязова М.Е.", false, true, true, now.AddDays(-1), null)
        }));
        return server;
    }

    private static void RunSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
