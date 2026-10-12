using System.Text.Json;

namespace BiStudent;

internal sealed class StarterData
{
    public string Version { get; set; } = "";
    public DateOnly SemesterStart { get; set; }
    public List<StarterStudent> Students { get; set; } = [];
    public List<StarterLesson> Lessons { get; set; } = [];
    public List<StarterCancellation> Cancellations { get; set; } = [];
    public static StarterData Load(string path) => JsonSerializer.Deserialize<StarterData>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidOperationException("Не удалось прочитать список студентов и расписание.");
}
internal sealed record StarterStudent(string Name, string GroupName);
internal sealed record StarterLesson(int SourceId, string Subject, string GroupName, DayOfWeek Day, TimeSpan Start, TimeSpan End, int Parity, string? Room, DateOnly ValidFrom, DateOnly? ValidTo, DateOnly? OneOffDate);
internal sealed record StarterCancellation(int SourceId, DateOnly Date, string Reason);
