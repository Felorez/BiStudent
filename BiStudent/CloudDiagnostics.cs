using System.Net.Http;
using System.Text.Json;

namespace BiStudent;

internal static class CloudDiagnostics
{
    public static async Task CheckCacheCloudAsync(string credentialsFile)
    {
        using var cloud = await ConnectAsync(credentialsFile).ConfigureAwait(false);
        var directory = Path.Combine(AppContext.BaseDirectory, "diagnostics"); Directory.CreateDirectory(directory);
        var workspace = new CloudWorkspace(cloud.LoadAsync, cloud.SaveChangesAsync, Path.Combine(directory, "cache-cloud-" + Guid.NewGuid().ToString("N"), "draft.json"));
        await workspace.LoadAsync().ConfigureAwait(false); var snapshot = workspace.Snapshot!;
        var anchor = snapshot.Grades.First(x => x.Score is null && !snapshot.Grades.Any(g => g.StudentId == x.StudentId && g.SubjectId == x.SubjectId && g.Score is not null));
        var preloaded = cloud.RequestsSent; var created = new List<string>();
        try
        {
            foreach (var score in new[] { 100, 100, 85 }) { workspace.Add(anchor.StudentId, anchor.SubjectId, score, DateOnly.FromDateTime(DateTime.Today), "Проверка пакетного сохранения"); created.Add(snapshot.Grades.Last().Id); }
            if (cloud.RequestsSent != preloaded) throw new InvalidOperationException("Редактирование делает запросы.");
            await workspace.SaveAsync().ConfigureAwait(false);
            if (workspace.HasChanges || cloud.RequestsSent != preloaded + 1) throw new InvalidOperationException(workspace.Error ?? "Оценки не сохранены одним запросом.");
            for (var i = 0; i < 10; i++) await workspace.SaveAsync().ConfigureAwait(false);
            if (cloud.RequestsSent != preloaded + 1) throw new InvalidOperationException("Idle делает запросы.");
            using var other = await ConnectAsync(credentialsFile).ConfigureAwait(false);
            var rows = (await other.LoadAsync().ConfigureAwait(false)).Grades.Where(g => created.Contains(g.Id)).ToList();
            if (rows.Count != 3 || CloudFirestore.Average(rows) != 95) throw new InvalidOperationException("Облако не получило пакет оценок.");
        }
        finally
        {
            foreach (var id in workspace.Snapshot!.Grades.Where(x => x.Comment == "Проверка пакетного сохранения").Select(x => x.Id).ToList()) workspace.Edit(id, null, null, "");
            await workspace.SaveAsync().ConfigureAwait(false);
            if (workspace.HasChanges) throw new InvalidOperationException(workspace.Error ?? "Не очищены тестовые оценки.");
        }
        File.WriteAllText(Path.Combine(directory, "cache-cloud-check.txt"), $"OK: preload requests={preloaded}; 3 edits=0 requests; batch save=1 request; 10 idle checks=0 requests; second login average=95; test grades cleared");
    }

    public static async Task CheckJournalAsync(string credentialsFile)
    {
        using var cloud = await ConnectAsync(credentialsFile).ConfigureAwait(false);
        var snapshot = await cloud.LoadAsync().ConfigureAwait(false);
        var directory = Path.Combine(AppContext.BaseDirectory, "diagnostics"); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "grades-before-private.json"), JsonSerializer.Serialize(snapshot));
        var anchor = snapshot.Grades.First(x => x.Score is null && !snapshot.Grades.Any(g => g.StudentId == x.StudentId && g.SubjectId == x.SubjectId && g.Score is not null));
        var created = new List<CloudGrade>();
        try
        {
            foreach (var score in new[] { 100, 100, 85 }) created.Add(await cloud.AddGradeAsync(anchor.StudentId, anchor.SubjectId, score, DateOnly.FromDateTime(DateTime.Today), "Проверка журнала").ConfigureAwait(false));
            using var other = await ConnectAsync(credentialsFile).ConfigureAwait(false);
            var loaded = (await other.LoadAsync().ConfigureAwait(false)).Grades.Where(x => x.StudentId == anchor.StudentId && x.SubjectId == anchor.SubjectId && x.Score is not null).ToList();
            if (loaded.Count != 3 || Math.Abs(CloudFirestore.Average(loaded)!.Value - 95) > 0.00001) throw new InvalidOperationException("Журнал не сохранил 100,100,85 со средним 95.");
            var first = loaded.Single(x => x.Id == created[0].Id);
            await cloud.SaveGradeAsync(first, 70, first.Date, first.Comment).ConfigureAwait(false);
            loaded = (await other.LoadAsync().ConfigureAwait(false)).Grades.Where(x => x.StudentId == anchor.StudentId && x.SubjectId == anchor.SubjectId && x.Score is not null).ToList();
            if (CloudFirestore.Average(loaded) != 85) throw new InvalidOperationException("Среднее после изменения неверно.");
            var hundred = Enumerable.Range(0, 100).Select(i => anchor with { Id = i.ToString(), Score = 100 }).ToList();
            CloudFirestore.CheckCapacity(hundred.Take(99));
            var rejected = false; try { CloudFirestore.CheckCapacity(hundred); } catch (InvalidOperationException) { rejected = true; }
            if (!rejected) throw new InvalidOperationException("101-я оценка не запрещена.");
        }
        finally
        {
            var latest = await cloud.LoadAsync().ConfigureAwait(false);
            foreach (var row in latest.Grades.Where(x => created.Any(g => g.Id == x.Id))) await cloud.SaveGradeAsync(row, null, null, "").ConfigureAwait(false);
        }
        var after = await cloud.LoadAsync().ConfigureAwait(false);
        foreach (var old in snapshot.Grades.Where(x => x.Score is not null))
            if (after.Grades.Single(x => x.Id == old.Id) is var row && (row.Score != old.Score || row.Date != old.Date || row.Comment != old.Comment)) throw new InvalidOperationException("Изменилась прежняя оценка.");
        File.WriteAllText(Path.Combine(directory, "journal-check.txt"), "OK: 100,100,85 persisted separately; other login average=95; edit average=85; limit 100; test grades cleared; existing grades preserved");
    }

    public static async Task CheckAsync(string credentialsFile)
    {
        using var cloud = await ConnectAsync(credentialsFile).ConfigureAwait(false);
        var snapshot = await cloud.LoadAsync().ConfigureAwait(false);
        if (snapshot.Students.Count != 23 || snapshot.Grades.Count != 138) throw new InvalidOperationException("Неверный размер группы.");
        var original = snapshot.Grades.First();
        var current = original;
        try
        {
            current = await cloud.SaveGradeAsync(current, 0, DateOnly.FromDateTime(DateTime.Today), "Проверка синхронизации").ConfigureAwait(false);
            using var other = await ConnectAsync(credentialsFile).ConfigureAwait(false);
            if ((await other.LoadAsync().ConfigureAwait(false)).Grades.Single(x => x.Id == current.Id).Score != 0) throw new InvalidOperationException("Другой вход не получил оценку.");
            var rejected = false;
            try { await cloud.SaveGradeAsync(original, 50, DateOnly.FromDateTime(DateTime.Today), "").ConfigureAwait(false); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("изменились")) { rejected = true; }
            if (!rejected) throw new InvalidOperationException("Нет защиты от перезаписи старой версии.");
            current = await cloud.SaveGradeAsync(current, 100, DateOnly.FromDateTime(DateTime.Today), "").ConfigureAwait(false);
            if ((await other.LoadAsync().ConfigureAwait(false)).Grades.Single(x => x.Id == current.Id).Score != 100) throw new InvalidOperationException("Не сохранён балл 100.");
        }
        finally { await cloud.SaveGradeAsync(current, original.Score, original.Date, original.Comment).ConfigureAwait(false); }
        using var anonymous = new HttpClient();
        using var response = await anonymous.GetAsync("https://firestore.googleapis.com/v1/projects/bistudent/databases/(default)/documents/groups/23-po-103-4r-2").ConfigureAwait(false);
        if (response.StatusCode != System.Net.HttpStatusCode.Forbidden) throw new InvalidOperationException("Анонимный доступ не закрыт.");
        var settings = FirebaseSettings.Load();
        var directory = Path.Combine(AppContext.BaseDirectory, "diagnostics", "fresh-device-" + Guid.NewGuid().ToString("N"));
        var store = new AttendanceStore(directory);
        store.ApplyCloudRoster(snapshot, settings.GroupId); store.ApplyCloudRoster(snapshot, settings.GroupId);
        if (store.Students().Count != 23 || store.Students().Any(x => x.SampleCount != 0)) throw new InvalidOperationException("Импорт на чистое устройство неверен.");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "diagnostics", "cloud-check.txt"), "OK: new login; scores 0/100; stale update rejected; original grade restored; anonymous access denied; fresh device 23 students; repeated import no duplicates");
    }

    public static async Task<CloudFirestore> ConnectAsync(string credentialsFile)
    {
        var settings = FirebaseSettings.Load();
        using var credentials = JsonDocument.Parse(File.ReadAllText(credentialsFile));
        if (credentials.RootElement.GetProperty("email").GetString() != settings.TeacherEmail) throw new InvalidOperationException("Неверная учётная запись в файле входа.");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var teacher = await new FirebaseAuth(settings, http).SignInAsync(credentials.RootElement.GetProperty("password").GetString()!).ConfigureAwait(false);
        return new CloudFirestore(settings, teacher);
    }

    public static async Task SeedAsync(string seedFile, string credentialsFile)
    {
        var seed = JsonSerializer.Deserialize<CloudSeed>(File.ReadAllText(seedFile), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        if (seed.Students.Count != 23 || seed.Subjects.Count != 6) throw new InvalidOperationException("Ожидалось 23 студента и 6 предметов.");
        using var cloud = await ConnectAsync(credentialsFile).ConfigureAwait(false);
        await cloud.SeedAsync(seed).ConfigureAwait(false);
        var snapshot = await cloud.LoadAsync().ConfigureAwait(false);
        if (snapshot.Students.Count != 23 || snapshot.Subjects.Count != 6 || snapshot.Grades.Count != 138 || snapshot.Students.Any(s => seed.Students.All(x => x.Id != s.Id || x.Name != s.Name)))
            throw new InvalidOperationException("Проверка данных Firestore не прошла.");
        var directory = Path.Combine(AppContext.BaseDirectory, "diagnostics"); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "cloud-result.txt"), "OK: students=23; subjects=6; grade-slots=138");
    }
}
