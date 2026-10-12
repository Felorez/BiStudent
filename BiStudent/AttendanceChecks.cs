using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace BiStudent;

internal static class AttendanceChecks
{
    public static async Task RunAsync(string directory)
    {
        var handler = new JournalHandler();
        using var cloud = new CloudFirestore(new FirebaseSettings { ProjectId = "test", GroupId = "test" },
            new TeacherSession("test", "test@example.invalid", "Тест", "test-token", "", DateTimeOffset.UtcNow.AddHours(1)), new HttpClient(handler));
        var snapshot = new CloudSnapshot("Т-1", [new("s01",1,"Тест")], [new("csharp","C#")], []);
        var store = new AttendanceStore(Path.Combine(directory, "attendance-test-" + Guid.NewGuid().ToString("N")));
        store.ApplyCloudRoster(snapshot,"test"); var student = store.Students().Single();
        store.AddLesson("C#","Т-1",DayOfWeek.Monday,TimeSpan.FromHours(9),TimeSpan.FromHours(10));
        var lesson = store.Lessons().Single(); var date = new DateOnly(2026,10,12);
        var sync = new AttendanceSync(store,cloud,snapshot);
        await sync.SaveAsync(); if (handler.Calls != 0) throw new Exception("Idle attendance sends requests.");
        store.MarkPresent(lesson.Id,date,student.Id,"Камера");
        if (!sync.HasChanges) throw new Exception("Camera attendance not pending.");
        handler.Fail = true;
        try { await sync.SaveAsync(); throw new Exception("Offline unexpectedly saved."); } catch (HttpRequestException) { }
        var restarted = new AttendanceSync(store,cloud,snapshot);
        if (!restarted.HasChanges) throw new Exception("Offline attendance lost on restart.");
        handler.Fail = false; await restarted.SaveAsync(); var count = handler.Calls;
        for (var i=0;i<10;i++) await restarted.SaveAsync();
        if (restarted.HasChanges || handler.Calls != count || handler.Rows.Count != 1) throw new Exception("Repeated attendance writes.");
        var remote = snapshot with { Attendance = handler.Rows.Values.ToList() };
        var other = new AttendanceStore(Path.Combine(directory,"attendance-device-" + Guid.NewGuid().ToString("N")));
        other.ApplyCloudRoster(snapshot,"test");
        var second = new AttendanceSync(other,cloud,remote);
        if (second.HasChanges || other.ExportAttendance("Т-1").Single().SeenAt != store.ExportAttendance("Т-1").Single().SeenAt) throw new Exception("New device did not restore attendance/time.");
        var otherLesson = other.Lessons().Single();
        other.RemoveMark(otherLesson.Id,date,other.Students().Single().Id);
        await second.SaveAsync();
        var removed = snapshot with { Attendance = handler.Rows.Values.ToList() };
        var firstAgain = new AttendanceSync(store,cloud,removed);
        if (firstAgain.HasChanges || store.ExportAttendance("Т-1").Count != 0) throw new Exception("Removed mark did not sync.");
        store.MarkPresent(lesson.Id,date,student.Id,"Вручную");
        await firstAgain.SaveAsync();
        if (!handler.Rows.Values.Single().Present || handler.Rows.Count != 1) throw new Exception("Re-mark created duplicate.");
        File.WriteAllText(Path.Combine(directory,"attendance-check.txt"),"OK: idle zero requests; offline/restart retains mark; batch save; second device restores lesson/time; removal syncs; re-mark no duplicates");
    }
    private sealed class JournalHandler : HttpMessageHandler
    {
        public int Calls; public bool Fail;
        public Dictionary<string,SyncedAttendance> Rows = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; if (Fail) throw new HttpRequestException("Offline");
            if (request.Method != HttpMethod.Post || !request.RequestUri!.AbsolutePath.EndsWith(":commit")) throw new Exception("Wrong attendance request.");
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            foreach (var write in json.RootElement.GetProperty("writes").EnumerateArray()) {
                var update = write.GetProperty("update");
                if (!update.GetProperty("name").GetString()!.Contains("/attendance/")) throw new Exception("Wrong collection.");
                var row = JsonSerializer.Deserialize<SyncedAttendance>(update.GetProperty("fields").GetProperty("payload").GetProperty("stringValue").GetString()!)!;
                Rows[row.Key] = row;
            }
            return new(HttpStatusCode.OK) { Content = new StringContent("{}",Encoding.UTF8,"application/json") };
        }
    }
}
