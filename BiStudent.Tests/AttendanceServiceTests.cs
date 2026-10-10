using BiStudent.Contracts;
using Xunit;

namespace BiStudent.Tests;

public sealed class AttendanceServiceTests
{
    [Fact]
    public async Task MarkAsync_UpsertsManualMarkAndCameraCannotOverwriteIt()
    {
        using var test = new TestDb();
        var teacher = test.AddTeacher();
        var group = test.AddGroup("ИВТ-1", "Иван");
        var slot = test.AddSlot(teacher, group);
        var date = new DateOnly(2026, 9, 1);
        var student = group.Students.Single();
        Assert.True(await test.Attendance.MarkAsync(slot.Id, date, student.Id, AttendanceStatus.Present, MarkMethod.Manual));
        Assert.True(await test.Attendance.MarkAsync(slot.Id, date, student.Id, AttendanceStatus.Late, MarkMethod.Manual));
        Assert.False(await test.Attendance.MarkAsync(slot.Id, date, student.Id, AttendanceStatus.Present, MarkMethod.Camera));
        var mark = Assert.Single(test.Db.AttendanceMarks);
        Assert.Equal(AttendanceStatus.Late, mark.Status);
        Assert.Equal(MarkMethod.Manual, mark.Method);
    }

    [Fact]
    public async Task MarkAsync_NotifiesOnlyForChanges()
    {
        using var test = new TestDb();
        var teacher = test.AddTeacher();
        var group = test.AddGroup("ИВТ-1", "Иван");
        var slot = test.AddSlot(teacher, group);
        var date = new DateOnly(2026, 9, 1);
        var calls = new List<(int SlotId, DateOnly Date)>();
        test.Notifier.Changed += (slotId, changedDate) => calls.Add((slotId, changedDate));
        Assert.True(await test.Attendance.MarkAsync(slot.Id, date, group.Students[0].Id, AttendanceStatus.Present, MarkMethod.Manual));
        Assert.False(await test.Attendance.MarkAsync(slot.Id, date, group.Students[0].Id, AttendanceStatus.Present, MarkMethod.Manual));
        Assert.Equal(new[] { (slot.Id, date) }, calls);
    }

    [Fact]
    public async Task MarkAsync_RejectsStudentFromAnotherGroup()
    {
        using var test = new TestDb();
        var teacher = test.AddTeacher();
        var group = test.AddGroup("ИВТ-1");
        var other = test.AddGroup("ИВТ-2", "Пётр");
        var slot = test.AddSlot(teacher, group);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            test.Attendance.MarkAsync(slot.Id, new DateOnly(2026, 9, 1), other.Students[0].Id,
                AttendanceStatus.Present, MarkMethod.Manual));
        Assert.Contains("не принадлежит", error.Message);
    }

    [Fact]
    public async Task MarkRestAbsentAsync_OnlyMarksUnmarkedStudents()
    {
        using var test = new TestDb();
        var teacher = test.AddTeacher();
        var group = test.AddGroup("ИВТ-1", "Иван", "Анна", "Пётр");
        var slot = test.AddSlot(teacher, group);
        var date = new DateOnly(2026, 9, 1);
        await test.Attendance.MarkAsync(slot.Id, date, group.Students[0].Id, AttendanceStatus.Present, MarkMethod.Manual);
        Assert.Equal(2, await test.Attendance.MarkRestAbsentAsync(slot.Id, date));
        Assert.Equal(0, await test.Attendance.MarkRestAbsentAsync(slot.Id, date));
        Assert.Equal(3, test.Db.AttendanceMarks.Count());
        Assert.Equal(AttendanceStatus.Present, test.Db.AttendanceMarks.Single(x => x.StudentId == group.Students[0].Id).Status);
        Assert.Equal(2, test.Db.AttendanceMarks.Count(x => x.Status == AttendanceStatus.Absent));
    }

    [Fact]
    public async Task JournalAsync_SkipsCancelledLessonsAndCalculatesPercent()
    {
        using var test = new TestDb();
        var teacher = test.AddTeacher();
        var group = test.AddGroup("ИВТ-1", "Иван");
        var slot = test.AddSlot(teacher, group);
        var first = new DateOnly(2026, 9, 1);
        var second = first.AddDays(7);
        var third = first.AddDays(14);
        await test.Schedule.CancelAsync(slot.Id, second, "Cancelled");
        await test.Attendance.MarkAsync(slot.Id, first, group.Students[0].Id, AttendanceStatus.Present, MarkMethod.Manual);
        await test.Attendance.MarkAsync(slot.Id, third, group.Students[0].Id, AttendanceStatus.Absent, MarkMethod.Manual);
        var journal = await test.Attendance.JournalAsync(teacher.Id, group.Id, first, third, null);
        Assert.Equal(2, journal.Columns.Count);
        Assert.DoesNotContain(journal.Columns, x => x.Date == second);
        var row = Assert.Single(journal.Students);
        Assert.Equal(new AttendanceStatus?[] { AttendanceStatus.Present, AttendanceStatus.Absent }, row.Statuses);
        Assert.Equal(50d, row.Percent);
    }
}
