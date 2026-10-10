using BiStudent.Contracts;
using BiStudent.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace BiStudent.Server.Services;

public sealed record GroupSummary(int Id, string Name, int Students, int WithFace);

public sealed class StudentService(AppDbContext db, RecognitionService recognition)
{
    public Task<List<GroupSummary>> GroupsAsync() => db.Groups.AsNoTracking().OrderBy(x => x.Name)
        .Select(x => new GroupSummary(x.Id, x.Name, x.Students.Count, x.Students.Count(s => s.Samples.Any()))).ToListAsync();

    public Task<List<Student>> ForGroupAsync(int groupId) => db.Students.AsNoTracking()
        .Where(x => x.GroupId == groupId).OrderBy(x => x.FullName).ToListAsync();

    public Task<bool> HasFace(int studentId) => db.FaceSamples.AnyAsync(x => x.StudentId == studentId);

    public async Task<HashSet<int>> WithFaceAsync(int groupId) => (await db.FaceSamples.AsNoTracking()
        .Where(x => x.Student.GroupId == groupId).Select(x => x.StudentId).Distinct().ToListAsync()).ToHashSet();

    public async Task<Group> AddGroupAsync(string name)
    {
        name = name.Trim();
        if (name.Length == 0) throw new InvalidOperationException("Укажите название группы.");
        if (await db.Groups.AnyAsync(x => x.Name == name)) throw new InvalidOperationException("Такая группа уже есть.");
        var group = new Group { Name = name };
        db.Groups.Add(group);
        await db.SaveChangesAsync();
        return group;
    }

    public async Task DeleteGroupAsync(int id)
    {
        var group = await db.Groups.FindAsync(id) ?? throw new KeyNotFoundException("Группа не найдена.");
        if (await db.Students.AnyAsync(x => x.GroupId == id) || await db.ScheduleSlots.AnyAsync(x => x.GroupId == id))
            throw new InvalidOperationException("Нельзя удалить группу со студентами или расписанием.");
        db.Groups.Remove(group);
        await db.SaveChangesAsync();
    }

    public async Task<Student> AddStudentAsync(int groupId, string fullName)
    {
        fullName = fullName.Trim();
        if (fullName.Length == 0) throw new InvalidOperationException("Укажите ФИО.");
        if (!await db.Groups.AnyAsync(x => x.Id == groupId)) throw new KeyNotFoundException("Группа не найдена.");
        if (await db.Students.AnyAsync(x => x.GroupId == groupId && x.FullName == fullName))
            throw new InvalidOperationException("Студент уже есть в группе.");
        var student = new Student { GroupId = groupId, FullName = fullName };
        db.Students.Add(student);
        await db.SaveChangesAsync();
        return student;
    }

    public async Task RenameStudentAsync(int id, string fullName)
    {
        var student = await db.Students.FindAsync(id) ?? throw new KeyNotFoundException("Студент не найден.");
        fullName = fullName.Trim();
        if (fullName.Length == 0) throw new InvalidOperationException("Укажите ФИО.");
        if (await db.Students.AnyAsync(x => x.Id != id && x.GroupId == student.GroupId && x.FullName == fullName))
            throw new InvalidOperationException("Студент уже есть в группе.");
        student.FullName = fullName;
        await db.SaveChangesAsync();
    }

    public async Task MoveStudentAsync(int id, int groupId)
    {
        var student = await db.Students.FindAsync(id) ?? throw new KeyNotFoundException("Студент не найден.");
        if (!await db.Groups.AnyAsync(x => x.Id == groupId)) throw new KeyNotFoundException("Группа не найдена.");
        if (await db.Students.AnyAsync(x => x.Id != id && x.GroupId == groupId && x.FullName == student.FullName))
            throw new InvalidOperationException("Студент уже есть в группе.");
        student.GroupId = groupId;
        await db.SaveChangesAsync();
    }

    public async Task DeleteStudentAsync(int id)
    {
        var student = await db.Students.FindAsync(id) ?? throw new KeyNotFoundException("Студент не найден.");
        db.Students.Remove(student);
        await db.SaveChangesAsync();
        recognition.Invalidate();
    }

    public async Task<(int Added, int Skipped)> ImportCsvAsync(Stream csv)
    {
        using var reader = new StreamReader(csv, leaveOpen: true);
        var added = 0;
        var skipped = 0;
        while (await reader.ReadLineAsync() is { } raw)
        {
            var parts = raw.Split(';').Select(x => x.Trim()).ToArray();
            if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0) continue;
            if (parts[0].Equals("ФИО", StringComparison.OrdinalIgnoreCase)) continue;
            var group = await db.Groups.SingleOrDefaultAsync(x => x.Name == parts[1]) ?? await AddGroupAsync(parts[1]);
            if (await db.Students.AnyAsync(x => x.GroupId == group.Id && x.FullName == parts[0])) { skipped++; continue; }
            db.Students.Add(new Student { GroupId = group.Id, FullName = parts[0] });
            await db.SaveChangesAsync();
            added++;
        }
        return (added, skipped);
    }
}
