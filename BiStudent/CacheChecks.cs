namespace BiStudent;

internal static class CacheChecks
{
    public static async Task RunAsync(string directory)
    {
        var loads = 0; var commits = 0; var fail = false;
        var anchor = new CloudGrade("s01_csharp", "s01", "csharp", null, null, "", "v1");
        var server = new List<CloudGrade> { anchor };
        Task<CloudSnapshot> Load() { loads++; return Task.FromResult(new CloudSnapshot("Тест", [new("s01", 1, "Тест")], [new("csharp", "C#")], server.ToList())); }
        Task<List<CloudGrade>> Save(List<CloudGrade> original, List<CloudGrade> changed)
        {
            commits++; if (fail) throw new HttpRequestException("Offline");
            var result = changed.Select(x => x with { UpdateTime = "v" + commits }).ToList();
            foreach (var row in result) { server.RemoveAll(x => x.Id == row.Id); server.Add(row); }
            return Task.FromResult(result);
        }
        var path = Path.Combine(directory, "cache-test-" + Guid.NewGuid().ToString("N"), "draft.json");
        var cache = new CloudWorkspace(Load, Save, path); await cache.LoadAsync();
        for (var i = 0; i < 10; i++) await cache.SaveAsync();
        if (loads != 1 || commits != 0 || cache.HasChanges) throw new InvalidOperationException("Idle вызывает запросы.");
        var day = DateOnly.FromDateTime(DateTime.Today);
        foreach (var score in new[] { 100, 100, 85 }) cache.Add("s01", "csharp", score, day, "");
        if (loads != 1 || commits != 0 || cache.PendingCount != 3 || CloudFirestore.Average(cache.Snapshot!.Grades) != 95) throw new InvalidOperationException("Локальное редактирование вызывает запросы или неверное среднее.");
        await cache.SaveAsync(); await cache.SaveAsync();
        if (commits != 1 || loads != 1 || cache.HasChanges) throw new InvalidOperationException("Оценки не сохраняются пакетом.");
        var grade = cache.Snapshot!.Grades.First(x => x.Score is not null);
        cache.Edit(grade.Id, 10, day, ""); cache.Edit(grade.Id, grade.Score, grade.Date, grade.Comment);
        if (cache.HasChanges) throw new InvalidOperationException("Возврат к исходной оценке остаётся изменением.");
        cache.Add("s01", "csharp", 50, day, ""); var added = cache.Snapshot.Grades.Last(); cache.Edit(added.Id, null, null, "");
        if (cache.HasChanges) throw new InvalidOperationException("Добавление и удаление черновика вызывает запись.");
        cache.Edit(grade.Id, 20, day, "Черновик"); fail = true; await cache.SaveAsync();
        if (!cache.HasChanges || cache.Error is null) throw new InvalidOperationException("Ошибка сети потеряла изменения.");
        var restored = new CloudWorkspace(Load, Save, path); await restored.LoadAsync();
        if (!restored.HasChanges || restored.Snapshot!.Grades.Single(x => x.Id == grade.Id).Score != 20) throw new InvalidOperationException("Черновик не восстановлен.");
        fail = false; await restored.SaveAsync();
        if (restored.HasChanges || server.Single(x => x.Id == grade.Id).Score != 20) throw new InvalidOperationException("Повторное сохранение не работает.");
        File.WriteAllText(Path.Combine(directory, "cache-check.txt"), "OK: preload once; idle zero requests; 3 local edits zero requests; one batch commit; revert no writes; deleted draft no writes; offline changes retained; restart restored; retry saved");
    }
}
