using BiStudent.Contracts;
using BiStudent.Server.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using OpenCvSharp;

namespace BiStudent.Server.Services;


public sealed class RecognitionService(IServiceScopeFactory scopes, IDataProtectionProvider protection, IConfiguration configuration) : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<(int TeacherId, int SlotId, DateOnly Date), Dictionary<int, int>> streaks = [];
    private FaceEngine? engine;
    private bool initialized;
    private string? error;
    private int revision;
    private int trainedRevision = -1;
    private IDataProtector Protector => protection.CreateProtector("BiStudent.FaceSamples");

    public bool IsAvailable { get { EnsureEngine(); lock (gate) return engine is not null; } }
    public string? Error { get { EnsureEngine(); lock (gate) return error; } }

    private void EnsureEngine()
    {
        lock (gate)
        {
            if (initialized) return;
            initialized = true;
            try { engine = new FaceEngine(); }
            catch (Exception ex) { error = ex.Message; }
        }
    }

    public void Invalidate()
    {
        lock (gate) { revision++; streaks.Clear(); }
    }

    private async Task TrainIfNeededAsync()
    {
        int version;
        lock (gate) { if (trainedRevision == revision) return; version = revision; }
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.FaceSamples.AsNoTracking().Select(x => new { x.StudentId, x.ProtectedImage }).ToListAsync();
        var samples = new List<(int StudentId, byte[] Image)>();
        foreach (var sample in stored)
        {
            try { samples.Add((sample.StudentId, Protector.Unprotect(sample.ProtectedImage))); }
            catch { /* Повреждённый образец не блокирует остальные. */ }
        }
        lock (gate)
        {
            if (version != revision || engine is null) return;
            engine.Train(samples);
            trainedRevision = version;
        }
    }

    public async Task<RecognitionResultDto> RecognizeAsync(int teacherId, int slotId, DateOnly date, byte[] jpeg)
    {
        EnsureEngine();
        if (!IsAvailable) return new RecognitionResultDto([], 0, 0, Error);
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var slot = await db.ScheduleSlots.AsNoTracking().SingleOrDefaultAsync(x => x.Id == slotId && x.TeacherId == teacherId)
            ?? throw new UnauthorizedAccessException("Пара недоступна преподавателю.");
        var students = await db.Students.AsNoTracking().Select(x => new { x.Id, x.FullName, x.GroupId }).ToDictionaryAsync(x => x.Id);
        var marked = await db.AttendanceMarks.AsNoTracking().Where(x => x.SlotId == slotId && x.Date == date)
            .Select(x => x.StudentId).ToListAsync();
        try { await TrainIfNeededAsync(); }
        catch (Exception ex) { return new RecognitionResultDto([], 0, 0, ex.Message); }
        using var frame = Cv2.ImDecode(jpeg, ImreadModes.Color);
        if (frame.Empty()) return new RecognitionResultDto([], 0, 0, "Не удалось прочитать кадр JPEG.");
        IReadOnlyList<FaceMatch> matches;
        try { lock (gate) matches = engine!.Scan(frame); }
        catch (Exception ex) { return new RecognitionResultDto([], frame.Width, frame.Height, ex.Message); }
        var eligible = matches.Where(x => x.StudentId is int id && students.TryGetValue(id, out var s) && s.GroupId == slot.GroupId)
            .Select(x => x.StudentId!.Value).ToHashSet();
        var ready = new HashSet<int>();
        lock (gate)
        {
            var key = (teacherId, slotId, date);
            if (!streaks.TryGetValue(key, out var counts)) streaks[key] = counts = [];
            foreach (var id in counts.Keys.Except(eligible).ToArray()) counts.Remove(id);
            foreach (var id in eligible)
            {
                counts[id] = counts.GetValueOrDefault(id) + 1;
                if (counts[id] == Math.Max(1, configuration.GetValue("Recognition:FramesInRow", 3))) ready.Add(id);
            }
        }
        var changed = new HashSet<int>();
        var attendance = scope.ServiceProvider.GetRequiredService<AttendanceService>();
        foreach (var id in ready)
            if (await attendance.MarkAsync(slotId, date, id, AttendanceStatus.Present, MarkMethod.Camera)) changed.Add(id);
        var faces = matches.Select(match => {
            var id = match.StudentId;
            var known = id is int value && students.TryGetValue(value, out var student) ? student : null;
            var other = known is not null && known.GroupId != slot.GroupId;
            return new RecognizedFaceDto(match.Bounds.X, match.Bounds.Y, match.Bounds.Width, match.Bounds.Height,
                id, known?.FullName, match.Similarity, id is int studentId && changed.Contains(studentId),
                id is int existing && marked.Contains(existing), other);
        }).ToList();
        return new RecognitionResultDto(faces, frame.Width, frame.Height);
    }

    public async Task<EnrollResultDto> EnrollAsync(int studentId, IReadOnlyList<byte[]> jpegs)
    {
        if (jpegs.Count < 3) return new EnrollResultDto(false, "Нужно не меньше трёх кадров.");
        EnsureEngine();
        if (!IsAvailable) return new EnrollResultDto(false, Error ?? "Распознавание недоступно.");
        var images = new List<byte[]>();
        for (var i = 0; i < jpegs.Count; i++)
        {
            try
            {
                using var frame = Cv2.ImDecode(jpegs[i], ImreadModes.Color);
                if (frame.Empty()) throw new InvalidOperationException("Не удалось прочитать JPEG.");
                lock (gate) images.Add(engine!.CaptureSample(frame, requireSingle: true));
            }
            catch (Exception ex) { return new EnrollResultDto(false, $"Кадр {i + 1}: {ex.Message}"); }
        }
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (!await db.Students.AnyAsync(x => x.Id == studentId)) return new EnrollResultDto(false, "Студент не найден.");
        var old = await db.FaceSamples.Where(x => x.StudentId == studentId).ToListAsync();
        db.FaceSamples.RemoveRange(old);
        db.FaceSamples.AddRange(images.Select(image => new FaceSample {
            StudentId = studentId, ProtectedImage = Protector.Protect(image), CreatedAt = DateTime.Now
        }));
        await db.SaveChangesAsync();
        Invalidate();
        return new EnrollResultDto(true, "Образцы лица сохранены.");
    }

    public void Dispose()
    {
        lock (gate) engine?.Dispose();
    }
}
