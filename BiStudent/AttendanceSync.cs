using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BiStudent;

internal sealed record SyncedAttendance(string StudentId, Lesson Lesson, string Date, string SeenAt, string Method, bool Present = true)
{
    public string Key => "a_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
        StudentId, Lesson.Subject, Lesson.GroupName, Lesson.Day, Lesson.Start, Lesson.End,
        Lesson.Parity, Lesson.Room, Lesson.ValidFrom, Lesson.ValidTo, Lesson.OneOffDate, Lesson.SemesterStart, Date
    })))).ToLowerInvariant();
}

// Only changed records are committed, so saving from one laptop cannot erase another laptop's journal.
internal sealed class AttendanceSync
{
    private readonly AttendanceStore store;
    private readonly CloudFirestore cloud;
    private readonly string group;
    private readonly string file;
    private Dictionary<string, SyncedAttendance> baseline;
    public AttendanceSync(AttendanceStore store, CloudFirestore cloud, CloudSnapshot snapshot)
    {
        this.store = store; this.cloud = cloud; group = snapshot.GroupName;
        file = Path.Combine(store.DataDirectory, "attendance-cloud-baseline.json");
        baseline = File.Exists(file) ? JsonSerializer.Deserialize<Dictionary<string, SyncedAttendance>>(File.ReadAllText(file)) ?? [] : [];
        var pending = Pending().Select(x => x.Key).ToHashSet();
        foreach (var row in snapshot.Attendance)
        {
            if (pending.Contains(row.Key)) continue;
            store.ApplyAttendance(row); baseline[row.Key] = row;
        }
        WriteBaseline();
    }
    private List<SyncedAttendance> Pending()
    {
        var current = store.ExportAttendance(group).ToDictionary(x => x.Key);
        var result = current.Where(x => !baseline.TryGetValue(x.Key, out var old) || old != x.Value).Select(x => x.Value).ToList();
        result.AddRange(baseline.Where(x => x.Value.Present && !current.ContainsKey(x.Key)).Select(x => x.Value with { Present = false }));
        return result;
    }
    public bool HasChanges => Pending().Count > 0;
    private void WriteBaseline() { File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(baseline)); File.Move(file + ".tmp", file, true); }
    public async Task SaveAsync()
    {
        var pending = Pending();
        foreach (var batch in pending.Chunk(200))
        {
            await cloud.SaveAttendanceAsync(batch);
            foreach (var row in batch) baseline[row.Key] = row;
            WriteBaseline();
        }
    }
}
