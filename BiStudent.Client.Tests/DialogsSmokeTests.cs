using System.Drawing.Imaging;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using BiStudent.Contracts;

namespace BiStudent.Client.Tests;

// Диалоги открываются поверх скрытого окна, загружают данные с фейкового сервера и закрываются без ошибок.
[Collection("settings")]
public sealed class DialogsSmokeTests
{
    [Fact]
    public void DialogsLoadAndClose()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var server = PagesSmokeTests.DemoServer();
        server.On(HttpMethod.Get, "api/groups/1/enrollment-code", _ => FakeServer.Json(new EnrollmentCodeDto(1, "23-ПО-103-4р-1", "K7PM-Q2XA",
            DateTime.Now, DateTime.Now.AddDays(1), ["http://192.168.0.10:5080/register/"])));
        server.On(HttpMethod.Get, "api/admin/backups", _ => FakeServer.Json(new List<BackupFileDto>
            { new("bistudent-2026-10-16.zip", DateTime.Now, 1_234_567, true) }));
        for (var index = 0; index < 3; index++)
            server.On(HttpMethod.Get, $"api/face-submissions/7/images/{index}", _ => JpegResponse());

        var slot = new SlotDto(10, 1, "23-ПО-103-4р-1", "Базы данных", "305", today.DayOfWeek,
            new TimeOnly(8, 30), new TimeOnly(9, 50), WeekParity.Every, new DateOnly(2026, 9, 1), null, null);
        var lesson = new LessonDto(slot, today, today.ToDateTime(slot.Start), today.ToDateTime(slot.End), false);
        var groups = new List<GroupDto> { new(1, "23-ПО-103-4р-1", 3, 1) };
        var opened = new List<string>();

        RunSta(() =>
        {
            var previousPath = ClientSettings.FilePath;
            var directory = Path.Combine(Path.GetTempPath(), "BiStudent.Tests", Guid.NewGuid().ToString("N"));
            ClientSettings.FilePath = Path.Combine(directory, "client.json");
            ExceptionDispatchInfo? failure = null;
            try
            {
                using var api = new ApiClient(new ClientSettings { ServerUrl = "http://test/" }, server);
                using var host = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), ShowInTaskbar = false };
                host.Shown += async (_, _) =>
                {
                    try
                    {
                        await api.LoginAsync("teacher", "secret");
                        await api.LoadMeAsync();
                        var dialogs = new (string Name, Func<Form> Create)[]
                        {
                            ("lesson", () => new LessonDialog(api, lesson)),
                            ("enrollment-code", () => new EnrollmentCodeDialog(api, 1, "23-ПО-103-4р-1")),
                            ("face-submissions", () => new FaceSubmissionsDialog(api)),
                            ("backups", () => new BackupDialog(api)),
                            ("slot-new", () => new SlotDialog(api, null, today, ["Базы данных"])),
                            ("slot-edit", () => new SlotDialog(api, slot, today, ["Базы данных"])),
                            ("student-new", () => new StudentDialog(api, groups, null, 1)),
                            ("student-edit", () => new StudentDialog(api, groups, new StudentDto(101, "Абдиев Артур", 1, true), 1))
                        };
                        foreach (var (name, create) in dialogs)
                        {
                            using var dialog = create();
                            dialog.StartPosition = FormStartPosition.Manual;
                            dialog.Location = new Point(-4000, -4000);
                            dialog.ShowInTaskbar = false;
                            dialog.Show(host);
                            await Task.Delay(500);
                            using var bitmap = new Bitmap(Math.Max(1, dialog.ClientSize.Width), Math.Max(1, dialog.ClientSize.Height));
                            dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.ClientSize));
                            dialog.Close();
                            opened.Add(name);
                        }
                        var column = new JournalColumnDto(JournalColumnKind.Lesson, 10, today, new TimeOnly(8, 30), "Базы данных");
                        using (new GradeEditor(api, column, new JournalCellDto(AttendanceStatus.Excused, ExcuseReason.Illness, "Справка", 80), 101, "Абдиев Артур")) { }
                        using (new GradeEditor(api, column, new JournalCellDto(null, null, null, null), 101, "Абдиев Артур", attendanceOnly: true)) { }
                        using (new GradeEditor(api, 1, "Базы данных", 101, "Абдиев Артур", 88)) { }
                        opened.Add("grade-editor");
                    }
                    catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
                    finally { host.Close(); }
                };
                Application.Run(host);
            }
            finally
            {
                ClientSettings.FilePath = previousPath;
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
            failure?.Throw();
        });

        Assert.Equal(["lesson", "enrollment-code", "face-submissions", "backups", "slot-new", "slot-edit", "student-new", "student-edit", "grade-editor"], opened);
        var missing = server.Requests.Where(r => !server.Handles(r)).Select(r => $"{r.Method} {r.Path}").Distinct().ToList();
        Assert.True(missing.Count == 0, "Запросы без ответа фейкового сервера: " + string.Join("; ", missing));
        Assert.Contains(server.Requests, r => r.Path.StartsWith("api/face-submissions/7/images/0", StringComparison.Ordinal));
    }

    private static HttpResponseMessage JpegResponse()
    {
        using var bitmap = new Bitmap(112, 112);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.SlateGray);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Jpeg);
        var content = new ByteArrayContent(stream.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
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
