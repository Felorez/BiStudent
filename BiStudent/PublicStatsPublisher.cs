using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BiStudent;

internal sealed record PublicAttendance(string CloudId, int LessonId, string Subject, string Date, string Time);

internal sealed class PublicStatsPublisher(CloudFirestore cloud, AttendanceStore store)
{
    private readonly string hashPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StudentAttendance", "public-statistics.sha256");
    public async Task PublishAsync(CloudSnapshot snapshot, bool force = false)
    {
        var students = snapshot.Students.OrderBy(x => x.Ordinal).ToList();
        var subjects = snapshot.Subjects;
        var attendance = store.PublicAttendance(snapshot.GroupName).Where(x => students.Any(s => s.Id == x.CloudId.Split('/').Last())).ToList();
        var lessons = attendance.GroupBy(x => x.LessonId).Select(x => new { id = x.Key, subject = x.First().Subject }).OrderBy(x => x.id).ToList();
        var grades = snapshot.Grades.Where(x => x.Score is not null).OrderBy(x => x.Id).Select(x => new object?[] { students.FindIndex(s => s.Id == x.StudentId), subjects.FindIndex(s => s.Id == x.SubjectId), x.Score, x.Date?.ToString("yyyy-MM-dd") }).Where(x => (int)x[0]! >= 0 && (int)x[1]! >= 0).ToArray();
        var payload = new
        {
            schema = 1, group = snapshot.GroupName, teacher = FirebaseSettings.Load().TeacherName,
            students = students.Select(x => new { id = x.Id, name = x.Name }).ToArray(),
            subjects = subjects.Select(x => new { id = x.Id, name = x.Name }).ToArray(),
            grades, lessons,
            attendance = attendance.Select(x => new object[] { students.FindIndex(s => s.Id == x.CloudId.Split('/').Last()), lessons.FindIndex(l => l.id == x.LessonId), x.Date, x.Time }).ToArray()
        };
        var json = JsonSerializer.Serialize(payload);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        if (!force && File.Exists(hashPath) && File.ReadAllText(hashPath) == hash) return;
        if (Encoding.UTF8.GetByteCount(json) > 900_000) throw new InvalidOperationException("Статистика превышает размер одного документа Firebase.");
        await cloud.PublishPublicStatsAsync(json);
        Directory.CreateDirectory(Path.GetDirectoryName(hashPath)!); File.WriteAllText(hashPath, hash);
    }
}
