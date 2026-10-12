using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace BiStudent;

internal sealed record CloudStudent(string Id, int Ordinal, string Name);
internal sealed record CloudSubject(string Id, string Name) { public override string ToString() => Name; }
internal sealed record CloudGrade(string Id, string StudentId, string SubjectId, int? Score, DateOnly? Date, string Comment, string UpdateTime);
internal sealed record CloudSnapshot(string GroupName, List<CloudStudent> Students, List<CloudSubject> Subjects, List<CloudGrade> Grades);
internal sealed record CloudSeed(string GroupName, List<CloudStudent> Students, List<CloudSubject> Subjects);

internal sealed class CloudFirestore : IDisposable
{
    internal int RequestsSent { get; private set; }
    public async Task PublishPublicStatsAsync(string payload)
    {
        using var result = await SendAsync(HttpMethod.Patch, DocumentRoot + "/publicStats/" + settings.GroupId,
            new { fields = Fields(new() { ["payload"] = payload, ["updatedAt"] = DateTimeOffset.UtcNow.ToString("O") }) }).ConfigureAwait(false);
    }
    public async Task<int> RenameTeacherAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://identitytoolkit.googleapis.com/v1/accounts:update?key=" + Uri.EscapeDataString(settings.ApiKey));
        request.Content = JsonContent.Create(new { idToken = await TokenAsync().ConfigureAwait(false), displayName = settings.TeacherName, returnSecureToken = false });
        using var profile = await http.SendAsync(request).ConfigureAwait(false);
        if (!profile.IsSuccessStatusCode) throw new InvalidOperationException("Не удалось изменить имя в Firebase Authentication.");
        var documents = await ListAsync("grades").ConfigureAwait(false);
        var old = documents.Where(d => Text(d.GetProperty("fields"), "teacherUid") == settings.TeacherUid && Text(d.GetProperty("fields"), "teacherName").StartsWith("Марал", StringComparison.Ordinal)).ToList();
        foreach (var batch in old.Chunk(400))
        {
            var writes = batch.Select(d => new { update = new { name = d.GetProperty("name").GetString(), fields = Fields(new() { ["teacherName"] = settings.TeacherName }) }, updateMask = new { fieldPaths = new[] { "teacherName" } }, currentDocument = new { updateTime = d.GetProperty("updateTime").GetString() } }).ToArray();
            using var result = await SendAsync(HttpMethod.Post, DocumentRoot + ":commit", new { writes }).ConfigureAwait(false);
        }
        return old.Count;
    }
    public const int MaximumGradesPerSubject = 100;
    public static double? Average(IEnumerable<CloudGrade> grades)
    {
        var values = grades.Where(x => x.Score is not null).Select(x => x.Score!.Value).ToArray();
        return values.Length == 0 ? null : values.Average();
    }
    internal static void CheckCapacity(IEnumerable<CloudGrade> grades)
    {
        if (grades.Count(x => x.Score is not null) >= MaximumGradesPerSubject) throw new InvalidOperationException("По этому предмету уже сохранено 100 оценок. Измените или удалите одну из записей.");
    }
    public async Task<List<CloudGrade>> SaveChangesAsync(List<CloudGrade> baseline, List<CloudGrade> changes)
    {
        if (changes.Count == 0) return [];
        var writes = new List<object>(); var documents = new List<CloudGrade>();
        foreach (var subject in changes.GroupBy(x => (x.StudentId, x.SubjectId)))
        {
            var rows = baseline.Where(x => x.StudentId == subject.Key.StudentId && x.SubjectId == subject.Key.SubjectId).ToDictionary(x => x.Id);
            foreach (var row in subject) rows[row.Id] = row;
            if (rows.Values.Count(x => x.Score is not null) > MaximumGradesPerSubject) throw new InvalidOperationException("Максимум 100 оценок по одному предмету.");
            var anchorId = subject.Key.StudentId + "_" + subject.Key.SubjectId;
            var anchor = baseline.Single(x => x.Id == anchorId);
            if (subject.All(x => x.Id != anchorId))
            {
                writes.Add(new { update = new { name = GroupPath + "/grades/" + anchorId, fields = Fields(new() { ["journalRevision"] = Guid.NewGuid().ToString("N") }) }, updateMask = new { fieldPaths = new[] { "journalRevision" } }, currentDocument = new { updateTime = anchor.UpdateTime } });
                documents.Add(anchor);
            }
            foreach (var grade in subject)
            {
                if (grade.Score is < 0 or > 100 || grade.Score is not null && grade.Date is null || grade.Comment.Length > 500) throw new ArgumentException("Некорректная оценка.");
                var fields = Fields(new Dictionary<string, object?> { ["studentId"] = grade.StudentId, ["subjectId"] = grade.SubjectId, ["score"] = grade.Score, ["date"] = grade.Date?.ToString("yyyy-MM-dd") ?? "", ["comment"] = grade.Comment, ["teacherUid"] = teacher.Uid, ["teacherName"] = teacher.Name });
                var original = baseline.FirstOrDefault(x => x.Id == grade.Id);
                if (original is null) writes.Add(new { update = new { name = GroupPath + "/grades/" + grade.Id, fields }, currentDocument = new { exists = false } });
                else writes.Add(new { update = new { name = GroupPath + "/grades/" + grade.Id, fields }, currentDocument = new { updateTime = original.UpdateTime } });
                documents.Add(grade);
            }
        }
        using var response = await SendAsync(HttpMethod.Post, DocumentRoot + ":commit", new { writes }).ConfigureAwait(false);
        var results = response!.RootElement.GetProperty("writeResults").EnumerateArray().ToList();
        return documents.Select((g, i) => g with { UpdateTime = results[i].GetProperty("updateTime").GetString()! }).ToList();
    }

    public async Task<CloudGrade> AddGradeAsync(string studentId, string subjectId, int score, DateOnly date, string comment)
    {
        if (score is < 0 or > 100 || comment.Length > 500) throw new ArgumentException("Оценка должна быть от 0 до 100, комментарий — до 500 символов.");
        var snapshot = await LoadAsync().ConfigureAwait(false);
        if (!snapshot.Students.Any(x => x.Id == studentId) || !snapshot.Subjects.Any(x => x.Id == subjectId)) throw new ArgumentException("Студент или предмет не найден.");
        var existing = snapshot.Grades.Where(x => x.StudentId == studentId && x.SubjectId == subjectId).ToList();
        CheckCapacity(existing);
        var anchor = existing.Single(x => x.Id == studentId + "_" + subjectId);
        var empty = existing.FirstOrDefault(x => x.Score is null);
        var id = empty?.Id ?? studentId + "_" + subjectId + "_" + Guid.NewGuid().ToString("N");
        var fields = Fields(new Dictionary<string, object?> { ["studentId"] = studentId, ["subjectId"] = subjectId, ["score"] = score, ["date"] = date.ToString("yyyy-MM-dd"), ["comment"] = comment.Trim(), ["teacherUid"] = teacher.Uid, ["teacherName"] = teacher.Name });
        var writes = new List<object>();
        if (id == anchor.Id)
        {
            writes.Add(new { update = new { name = GroupPath + "/grades/" + id, fields }, currentDocument = new { updateTime = anchor.UpdateTime } });
        }
        else
        {
            // Serialize additions to this student's subject to enforce the cap across devices.
            writes.Add(new { update = new { name = GroupPath + "/grades/" + anchor.Id, fields = Fields(new() { ["journalRevision"] = Guid.NewGuid().ToString("N") }) }, updateMask = new { fieldPaths = new[] { "journalRevision" } }, currentDocument = new { updateTime = anchor.UpdateTime } });
            if (empty is not null) writes.Add(new { update = new { name = GroupPath + "/grades/" + id, fields }, currentDocument = new { updateTime = empty.UpdateTime } });
            else writes.Add(new { update = new { name = GroupPath + "/grades/" + id, fields }, currentDocument = new { exists = false } });
        }
        using var result = await SendAsync(HttpMethod.Post, DocumentRoot + ":commit", new { writes }).ConfigureAwait(false);
        using var saved = await SendAsync(HttpMethod.Get, GroupPath + "/grades/" + id).ConfigureAwait(false);
        return ParseGrade(saved!.RootElement);
    }

    private readonly FirebaseSettings settings;
    private readonly TeacherSession teacher;
    private readonly HttpClient http;
    private readonly SemaphoreSlim tokenGate = new(1, 1);
    private string token;
    private string refreshToken;
    private DateTimeOffset expiresAt;
    private string DocumentRoot => $"projects/{settings.ProjectId}/databases/(default)/documents";
    private string GroupPath => $"{DocumentRoot}/groups/{settings.GroupId}";
    private const string Api = "https://firestore.googleapis.com/v1/";

    public CloudFirestore(FirebaseSettings settings, TeacherSession teacher, HttpClient? http = null)
    {
        this.settings = settings; this.teacher = teacher;
        this.http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        token = teacher.IdToken; refreshToken = teacher.RefreshToken; expiresAt = teacher.ExpiresAt;
    }

    private async Task<string> TokenAsync()
    {
        await tokenGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (string.IsNullOrEmpty(token)) throw new InvalidOperationException("Войдите в Firebase заново.");
            if (DateTimeOffset.UtcNow < expiresAt.AddMinutes(-2)) return token;
            using var response = await http.PostAsync("https://securetoken.googleapis.com/v1/token?key=" + Uri.EscapeDataString(settings.ApiKey),
                new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken })).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Сеанс Firebase завершён. Выйдите и войдите заново.");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            if (json.RootElement.GetProperty("user_id").GetString() != settings.TeacherUid) throw new InvalidOperationException("Firebase вернул другую учётную запись.");
            token = json.RootElement.GetProperty("id_token").GetString()!;
            refreshToken = json.RootElement.GetProperty("refresh_token").GetString()!;
            expiresAt = DateTimeOffset.UtcNow.AddSeconds(int.Parse(json.RootElement.GetProperty("expires_in").GetString()!));
            return token;
        }
        finally { tokenGate.Release(); }
    }

    private async Task<JsonDocument?> SendAsync(HttpMethod method, string path, object? body = null, bool missingAllowed = false)
    {
        using var request = new HttpRequestMessage(method, Api + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync().ConfigureAwait(false));
        if (body is not null) request.Content = JsonContent.Create(body);
        RequestsSent++;
        using var response = await http.SendAsync(request).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound && missingAllowed) return null;
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(response.StatusCode switch
            {
                HttpStatusCode.Forbidden => "Firestore отклонил доступ. Проверьте правила для Мадина Апай.",
                HttpStatusCode.NotFound => "Группа не найдена в Firestore. Проверьте, что данные загружены.",
                HttpStatusCode.Unauthorized => "Сеанс Firebase завершён. Войдите заново.",
                HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed => "Данные изменились на другом устройстве. Обновите список и повторите.",
                _ when text.Contains("FAILED_PRECONDITION", StringComparison.Ordinal) => "Данные изменились на другом устройстве. Обновите список и повторите.",
                _ => "Не удалось получить или сохранить данные в Firestore. Попробуйте ещё раз."
            });
        }
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
    }

    private async Task<List<JsonElement>> ListAsync(string collection)
    {
        var result = new List<JsonElement>(); string? page = null;
        do
        {
            using var json = await SendAsync(HttpMethod.Get, GroupPath + "/" + collection + "?pageSize=300" + (page is null ? "" : "&pageToken=" + Uri.EscapeDataString(page))).ConfigureAwait(false);
            if (json!.RootElement.TryGetProperty("documents", out var docs)) result.AddRange(docs.EnumerateArray().Select(x => x.Clone()));
            page = json.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
        } while (!string.IsNullOrEmpty(page));
        return result;
    }

    public async Task<CloudSnapshot> LoadAsync()
    {
        using var group = await SendAsync(HttpMethod.Get, GroupPath).ConfigureAwait(false);
        var fields = group!.RootElement.GetProperty("fields");
        var subjects = fields.GetProperty("subjects").GetProperty("arrayValue").GetProperty("values").EnumerateArray()
            .Select(x => { var item = x.GetProperty("mapValue").GetProperty("fields"); return new CloudSubject(Text(item, "id"), Text(item, "name")); }).ToList();
        var studentsTask = ListAsync("students"); var gradesTask = ListAsync("grades");
        await Task.WhenAll(studentsTask, gradesTask).ConfigureAwait(false);
        var students = studentsTask.Result.Select(doc => { var f = doc.GetProperty("fields"); return new CloudStudent(Id(doc), int.Parse(f.GetProperty("ordinal").GetProperty("integerValue").GetString()!), Text(f, "name")); }).OrderBy(x => x.Ordinal).ToList();
        var grades = gradesTask.Result.Select(ParseGrade).ToList();
        if (subjects.Count != 6 || subjects.Select(x => x.Id).Distinct().Count() != 6) throw new InvalidOperationException("В Firestore должен быть список из шести предметов.");
        return new CloudSnapshot(Text(fields, "name"), students, subjects, grades);
    }

    public async Task<CloudGrade> SaveGradeAsync(CloudGrade grade, int? score, DateOnly? date, string comment)
    {
        if (score is < 0 or > 100 || comment.Length > 500) throw new ArgumentException("Оценка должна быть от 0 до 100, комментарий — до 500 символов.");
        if (score is not null && date is null) throw new ArgumentException("Укажите дату оценки.");
        var fields = Fields(new Dictionary<string, object?> { ["score"] = score, ["date"] = date?.ToString("yyyy-MM-dd") ?? "", ["comment"] = comment.Trim(), ["teacherUid"] = teacher.Uid, ["teacherName"] = teacher.Name });
        var mask = string.Join("&", fields.Keys.Select(x => "updateMask.fieldPaths=" + x));
        using var result = await SendAsync(HttpMethod.Patch, GroupPath + "/grades/" + Uri.EscapeDataString(grade.Id) + "?" + mask + "&currentDocument.updateTime=" + Uri.EscapeDataString(grade.UpdateTime), new { fields }).ConfigureAwait(false);
        return ParseGrade(result!.RootElement);
    }

    public async Task SeedAsync(CloudSeed seed)
    {
        var existingStudents = await ListAsync("students").ConfigureAwait(false);
        var existingGrades = await ListAsync("grades").ConfigureAwait(false);
        using var group = await SendAsync(HttpMethod.Get, GroupPath, missingAllowed: true).ConfigureAwait(false);
        var writes = new List<object>();
        void Add(string path, Dictionary<string, object?> values) => writes.Add(new { update = new { name = path, fields = Fields(values) }, currentDocument = new { exists = false } });
        if (group is null) Add(GroupPath, new() { ["name"] = seed.GroupName, ["subjects"] = seed.Subjects.Select(x => new Dictionary<string, object?> { ["id"] = x.Id, ["name"] = x.Name }).ToArray() });
        var studentIds = existingStudents.Select(Id).ToHashSet(); var gradeIds = existingGrades.Select(Id).ToHashSet();
        foreach (var student in seed.Students)
        {
            if (!studentIds.Contains(student.Id)) Add(GroupPath + "/students/" + student.Id, new() { ["name"] = student.Name, ["ordinal"] = student.Ordinal });
            foreach (var subject in seed.Subjects)
            {
                var id = student.Id + "_" + subject.Id;
                if (!gradeIds.Contains(id)) Add(GroupPath + "/grades/" + id, new() { ["studentId"] = student.Id, ["subjectId"] = subject.Id, ["score"] = null, ["date"] = "", ["comment"] = "", ["teacherUid"] = teacher.Uid, ["teacherName"] = teacher.Name });
            }
        }
        if (writes.Count > 500) throw new InvalidOperationException("Слишком много данных для тестового импорта.");
        if (writes.Count > 0) { using var result = await SendAsync(HttpMethod.Post, DocumentRoot + ":commit", new { writes }).ConfigureAwait(false); }
    }

    private static string Id(JsonElement document) => document.GetProperty("name").GetString()!.Split('/').Last();
    private static string Text(JsonElement fields, string key) => fields.TryGetProperty(key, out var value) && value.TryGetProperty("stringValue", out var text) ? text.GetString() ?? "" : "";
    private static CloudGrade ParseGrade(JsonElement document)
    {
        var f = document.GetProperty("fields");
        var score = f.GetProperty("score"); var date = Text(f, "date");
        return new CloudGrade(Id(document), Text(f, "studentId"), Text(f, "subjectId"), score.TryGetProperty("integerValue", out var number) ? int.Parse(number.GetString()!) : null,
            date.Length > 0 ? DateOnly.ParseExact(date, "yyyy-MM-dd") : null, Text(f, "comment"), document.GetProperty("updateTime").GetString()!);
    }

    private static Dictionary<string, object> Fields(Dictionary<string, object?> values) => values.ToDictionary(x => x.Key, x => Value(x.Value));
    private static object Value(object? value) => value switch
    {
        null => new { nullValue = (object?)null },
        int number => new { integerValue = number.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        string text => new { stringValue = text },
        Dictionary<string, object?> map => new { mapValue = new { fields = Fields(map) } },
        IEnumerable<Dictionary<string, object?>> array => new { arrayValue = new { values = array.Select(Value).ToArray() } },
        _ => throw new ArgumentException("Неизвестный тип значения Firestore.")
    };
    public void Dispose() { http.Dispose(); tokenGate.Dispose(); token = ""; refreshToken = ""; }
}
