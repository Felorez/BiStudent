using System.Text.Json;

namespace BiStudent;

internal sealed record GradeDraft(List<CloudGrade> Original, List<CloudGrade> Changed);

// One session cache shared by all windows. Timer checks this cache, not Firebase.
internal sealed class CloudWorkspace
{
    private readonly Func<Task<CloudSnapshot>> load;
    private readonly Func<List<CloudGrade>, List<CloudGrade>, Task<List<CloudGrade>>> persist;
    private readonly string draftPath;
    private List<CloudGrade> baseline = [];
    public CloudSnapshot? Snapshot { get; private set; }
    public bool IsSaving { get; private set; }
    public string? Error { get; private set; }
    public event Action? Changed;
    public int PendingCount => Pending().Count;
    public bool HasChanges => PendingCount > 0;

    public CloudWorkspace(CloudFirestore cloud, string uid) : this(cloud.LoadAsync, cloud.SaveChangesAsync,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StudentAttendance", "pending-grades-" + uid + ".json")) { }

    internal CloudWorkspace(Func<Task<CloudSnapshot>> load, Func<List<CloudGrade>, List<CloudGrade>, Task<List<CloudGrade>>> persist, string draftPath)
    { this.load = load; this.persist = persist; this.draftPath = draftPath; }

    private static bool Equal(CloudGrade a, CloudGrade b) => a.StudentId == b.StudentId && a.SubjectId == b.SubjectId && a.Score == b.Score && a.Date == b.Date && a.Comment == b.Comment;
    private List<CloudGrade> Pending()
    {
        var original = baseline.ToDictionary(x => x.Id);
        return Snapshot?.Grades.Where(g => !original.TryGetValue(g.Id, out var old) ? g.Score is not null : !Equal(g, old)).ToList() ?? [];
    }

    public async Task LoadAsync()
    {
        if (IsSaving || HasChanges) throw new InvalidOperationException("Сначала сохраните изменения. Обновление не должно удалить несохранённые оценки.");
        var snapshot = await load();
        baseline = snapshot.Grades.ToList(); Snapshot = snapshot; Error = null;
        if (File.Exists(draftPath))
        {
            var draft = JsonSerializer.Deserialize<GradeDraft>(File.ReadAllText(draftPath));
            if (draft is not null)
            {
                foreach (var original in draft.Original) { var server=baseline.FirstOrDefault(x=>x.Id==original.Id); baseline.RemoveAll(x => x.Id == original.Id); baseline.Add(server is not null && Equal(server,original) ? server : original); }
                foreach (var grade in draft.Changed) { snapshot.Grades.RemoveAll(x => x.Id == grade.Id); snapshot.Grades.Add(grade); }
            }
        }
        Changed?.Invoke();
    }

    public void Add(string studentId, string subjectId, int score, DateOnly date, string comment)
    {
        Validate(score, date, comment);
        if (Snapshot is null || !Snapshot.Students.Any(x => x.Id == studentId) || !Snapshot.Subjects.Any(x => x.Id == subjectId)) throw new InvalidOperationException("Группа ещё не загружена.");
        CloudFirestore.CheckCapacity(Snapshot.Grades.Where(x => x.StudentId == studentId && x.SubjectId == subjectId));
        Snapshot.Grades.Add(new CloudGrade(studentId + "_" + subjectId + "_" + Guid.NewGuid().ToString("N"), studentId, subjectId, score, date, comment.Trim(), ""));
        LocalChanged();
    }

    public void Edit(string id, int? score, DateOnly? date, string comment)
    {
        Validate(score, date, comment);
        var grades = Snapshot?.Grades ?? throw new InvalidOperationException("Группа ещё не загружена.");
        var index = grades.FindIndex(x => x.Id == id); if (index < 0) throw new InvalidOperationException("Оценка не найдена.");
        var old = grades[index]; var edited = old with { Score = score, Date = date, Comment = comment.Trim() };
        if (Equal(old, edited)) return;
        if (score is null && baseline.All(x => x.Id != id)) grades.RemoveAt(index); else grades[index] = edited;
        LocalChanged();
    }

    private static void Validate(int? score, DateOnly? date, string comment)
    { if (score is < 0 or > 100 || score is not null && date is null || comment.Length > 500) throw new ArgumentException("Оценка: 0–100, дата обязательна, комментарий до 500 символов."); }

    private void LocalChanged() { Error = null; WriteDraft(); Changed?.Invoke(); }
    private void WriteDraft()
    {
        var pending = Pending(); Directory.CreateDirectory(Path.GetDirectoryName(draftPath)!);
        var text = JsonSerializer.Serialize(new GradeDraft(baseline.Where(x => pending.Any(g => g.Id == x.Id) || pending.Any(g => x.Id == g.StudentId + "_" + g.SubjectId)).ToList(), pending));
        for (var attempt = 0; ; attempt++)
        {
            try { File.WriteAllText(draftPath + ".tmp", text); File.Move(draftPath + ".tmp", draftPath, true); break; }
            catch (Exception ex) when (attempt < 3 && ex is IOException or UnauthorizedAccessException) { Thread.Sleep(30); }
        }
    }

    public async Task SaveAsync()
    {
        if (IsSaving || !HasChanges) return;
        var pending = Pending(); IsSaving = true; Error = null; Changed?.Invoke();
        try
        {
            foreach (var batch in pending.GroupBy(x => (x.StudentId, x.SubjectId)))
            {
                var saved = await persist(baseline.ToList(), batch.ToList());
                foreach (var grade in saved)
                {
                    baseline.RemoveAll(x => x.Id == grade.Id); baseline.Add(grade);
                    var index = Snapshot!.Grades.FindIndex(x => x.Id == grade.Id);
                    if (index >= 0) Snapshot.Grades[index] = Snapshot.Grades[index] with { UpdateTime = grade.UpdateTime };
                }
                WriteDraft();
            }
        }
        catch (Exception ex) { Error = ex is InvalidOperationException ? ex.Message : "Не удалось сохранить в Firebase. Изменения остались на компьютере; повторите сохранение."; }
        finally { IsSaving = false; Changed?.Invoke(); }
    }
}

internal sealed class SavePreferences
{
    public int Seconds { get; set; } = 5;
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StudentAttendance", "autosave.json");
    public static SavePreferences Load() { try { var value = JsonSerializer.Deserialize<SavePreferences>(File.ReadAllText(FilePath)); if (value?.Seconds is >= 1 and <= 3600) return value; } catch { } return new(); }
    public void Save() { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); File.WriteAllText(FilePath, JsonSerializer.Serialize(this)); }
}
