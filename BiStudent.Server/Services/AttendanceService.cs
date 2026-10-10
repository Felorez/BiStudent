using BiStudent.Contracts;
using BiStudent.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace BiStudent.Server.Services;

public sealed record AttendanceRow(int StudentId, string FullName, AttendanceStatus? Status, MarkMethod? Method, DateTime? MarkedAt);
public sealed record JournalColumn(int SlotId, DateOnly Date, TimeOnly Start, string Subject);
public sealed record JournalStudent(int StudentId, string FullName, List<AttendanceStatus?> Statuses, double Percent);
public sealed record JournalMatrix(string TeacherName, string GroupName, DateOnly From, DateOnly To, List<JournalColumn> Columns, List<JournalStudent> Students);

public sealed class AttendanceNotifier
{
    public event Action<int, DateOnly>? Changed;
    public void Notify(int slotId, DateOnly date) => Changed?.Invoke(slotId, date);
}

public sealed class AttendanceService(AppDbContext db, IConfiguration configuration, ScheduleService schedule, AttendanceNotifier notifier)
{
    public async Task<List<AttendanceRow>> ForLessonAsync(int slotId, DateOnly date)
    {
        var slot = await db.ScheduleSlots.FindAsync(slotId) ?? throw new KeyNotFoundException("Пара не найдена.");
        var students = await db.Students.AsNoTracking().Where(x => x.GroupId == slot.GroupId).OrderBy(x => x.FullName).ToListAsync();
        var marks = await db.AttendanceMarks.AsNoTracking().Where(x => x.SlotId == slotId && x.Date == date).ToDictionaryAsync(x => x.StudentId);
        return students.Select(x => {
            marks.TryGetValue(x.Id, out var mark);
            return new AttendanceRow(x.Id, x.FullName, mark?.Status, mark?.Method, mark?.MarkedAt);
        }).ToList();
    }

    public async Task<bool> MarkAsync(int slotId, DateOnly date, int studentId, AttendanceStatus status, MarkMethod method)
    {
        var slot = await db.ScheduleSlots.FindAsync(slotId) ?? throw new KeyNotFoundException("Пара не найдена.");
        if (!await db.Students.AnyAsync(x => x.Id == studentId && x.GroupId == slot.GroupId))
            throw new InvalidOperationException("Студент не принадлежит группе этой пары.");
        var now = DateTime.Now;
        if (method == MarkMethod.Camera)
        {
            var minutes = configuration.GetValue("Attendance:LateAfterMinutes", 10);
            status = now < date.ToDateTime(slot.Start).AddMinutes(minutes) ? AttendanceStatus.Present : AttendanceStatus.Late;
        }
        var mark = await db.AttendanceMarks.SingleOrDefaultAsync(x => x.SlotId == slotId && x.Date == date && x.StudentId == studentId);
        if (mark is not null && (method == MarkMethod.Camera && mark.Method == MarkMethod.Manual ||
            mark.Status == status && mark.Method == method)) return false;
        if (mark is null)
        {
            mark = new AttendanceMark { SlotId = slotId, Date = date, StudentId = studentId };
            db.AttendanceMarks.Add(mark);
        }
        mark.Status = status;
        mark.Method = method;
        mark.MarkedAt = now;
        await db.SaveChangesAsync();
        notifier.Notify(slotId, date);
        return true;
    }

    public async Task ClearAsync(int slotId, DateOnly date, int studentId)
    {
        var mark = await db.AttendanceMarks.SingleOrDefaultAsync(x => x.SlotId == slotId && x.Date == date && x.StudentId == studentId);
        if (mark is null) return;
        db.AttendanceMarks.Remove(mark);
        await db.SaveChangesAsync();
        notifier.Notify(slotId, date);
    }

    public async Task<int> MarkRestAbsentAsync(int slotId, DateOnly date)
    {
        var slot = await db.ScheduleSlots.FindAsync(slotId) ?? throw new KeyNotFoundException("Пара не найдена.");
        var marked = await db.AttendanceMarks.Where(x => x.SlotId == slotId && x.Date == date).Select(x => x.StudentId).ToListAsync();
        var missing = await db.Students.Where(x => x.GroupId == slot.GroupId && !marked.Contains(x.Id)).Select(x => x.Id).ToListAsync();
        var now = DateTime.Now;
        db.AttendanceMarks.AddRange(missing.Select(id => new AttendanceMark {
            SlotId = slotId, Date = date, StudentId = id, Status = AttendanceStatus.Absent, Method = MarkMethod.Manual, MarkedAt = now
        }));
        if (missing.Count > 0) { await db.SaveChangesAsync(); notifier.Notify(slotId, date); }
        return missing.Count;
    }

    public async Task<JournalMatrix> JournalAsync(int teacherId, int groupId, DateOnly from, DateOnly to, string? subject)
    {
        if (to < from) throw new InvalidOperationException("Конец периода раньше начала.");
        var teacher = await db.Teachers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == teacherId) ?? throw new KeyNotFoundException("Преподаватель не найден.");
        var group = await db.Groups.AsNoTracking().FirstOrDefaultAsync(x => x.Id == groupId) ?? throw new KeyNotFoundException("Группа не найдена.");
        var last = DateOnly.FromDateTime(DateTime.Now);
        if (to > last) to = last;
        var occurrences = new List<LessonOccurrence>();
        for (var day = from; day <= to; day = day.AddDays(1))
            occurrences.AddRange((await schedule.ForDayAsync(teacherId, day)).Where(x =>
                !x.Cancelled && x.Slot.GroupId == groupId &&
                (subject is null || x.Slot.Subject == subject) && x.EndsAt <= DateTime.Now));
        var columns = occurrences.OrderBy(x => x.StartsAt)
            .Select(x => new JournalColumn(x.Slot.Id, x.Date, x.Slot.Start, x.Slot.Subject)).ToList();
        var students = await db.Students.AsNoTracking().Where(x => x.GroupId == groupId).OrderBy(x => x.FullName).ToListAsync();
        var slotIds = columns.Select(x => x.SlotId).Distinct().ToArray();
        var marks = await db.AttendanceMarks.AsNoTracking().Where(x => slotIds.Contains(x.SlotId) && x.Date >= from && x.Date <= to).ToListAsync();
        var lookup = marks.ToDictionary(x => (x.SlotId, x.Date, x.StudentId), x => x.Status);
        var rows = students.Select(student => {
            var statuses = columns.Select(column =>
                lookup.TryGetValue((column.SlotId, column.Date, student.Id), out var value) ? (AttendanceStatus?)value : null).ToList();
            var present = statuses.Count(x => x is AttendanceStatus.Present or AttendanceStatus.Late);
            return new JournalStudent(student.Id, student.FullName, statuses, columns.Count == 0 ? 0 : 100d * present / columns.Count);
        }).ToList();
        return new JournalMatrix(teacher.FullName, group.Name, from, to, columns, rows);
    }
}
