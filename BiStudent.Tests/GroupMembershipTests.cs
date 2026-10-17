using BiStudent.Contracts;
using BiStudent.Server.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BiStudent.Tests;

public sealed class GroupMembershipTests
{
    [Fact]
    public async Task JournalsKeepPastMarksAndExcludeEarlierLessonsInNewGroup()
    {
        using var test = new TestDb();
        var teacher = test.AddTeacher();
        var groupA = test.AddGroup("Группа А", "Анна Иванова");
        var groupB = test.AddGroup("Группа Б");
        var student = Assert.Single(groupA.Students);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var past = today.AddDays(-2);
        var slotA = test.AddSlot(teacher, groupA, past.DayOfWeek, validFrom: past, oneOffDate: past);
        test.AddSlot(teacher, groupB, past.DayOfWeek, validFrom: past, oneOffDate: past);

        await test.Attendance.MarkAsync(slotA.Id, past, student.Id, AttendanceStatus.Present, MarkMethod.Manual);
        await test.Students.MoveStudentAsync(student.Id, groupB.Id);

        var journalA = await test.Attendance.JournalAsync(teacher.Id, groupA.Id, past, past, null);
        var rowA = Assert.Single(journalA.Students);
        Assert.Equal(student.Id, rowA.StudentId);
        Assert.Equal(AttendanceStatus.Present, Assert.Single(rowA.Cells).Status);
        Assert.Equal(100, rowA.Percent);

        var pastJournalB = await test.Attendance.JournalAsync(teacher.Id, groupB.Id, past, past, null);
        Assert.Empty(pastJournalB.Students);
        var journalB = await test.Attendance.JournalAsync(teacher.Id, groupB.Id, past, today, null);
        var rowB = Assert.Single(journalB.Students);
        Assert.False(Assert.Single(rowB.Cells).InGroup);
        Assert.Null(rowB.Cells[0].Status);
        Assert.Equal(0, rowB.Percent);
    }

    [Fact]
    public async Task PastLessonAcceptsFormerMemberButTodayDoesNot()
    {
        using var test = new TestDb();
        var teacher = test.AddTeacher();
        var groupA = test.AddGroup("Группа А", "Анна Иванова");
        var groupB = test.AddGroup("Группа Б");
        var student = Assert.Single(groupA.Students);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var past = today.AddDays(-2);
        var pastSlot = test.AddSlot(teacher, groupA, past.DayOfWeek, validFrom: past, oneOffDate: past);
        var todaySlot = test.AddSlot(teacher, groupA, today.DayOfWeek, validFrom: today, oneOffDate: today);

        await test.Students.MoveStudentAsync(student.Id, groupB.Id);

        Assert.True(await test.Attendance.MarkAsync(pastSlot.Id, past, student.Id,
            AttendanceStatus.Present, MarkMethod.Manual));
        Assert.Contains(await test.Attendance.ForLessonAsync(pastSlot.Id, past), x => x.StudentId == student.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Attendance.MarkAsync(
            todaySlot.Id, today, student.Id, AttendanceStatus.Present, MarkMethod.Manual));
    }

    [Fact]
    public async Task TwoMovesTodayKeepOnlyOriginalAndCurrentMemberships()
    {
        using var test = new TestDb();
        var groupA = test.AddGroup("Группа А", "Анна Иванова");
        var groupB = test.AddGroup("Группа Б");
        var groupC = test.AddGroup("Группа В");
        var student = Assert.Single(groupA.Students);
        var today = DateOnly.FromDateTime(DateTime.Today);

        await test.Students.MoveStudentAsync(student.Id, groupB.Id);
        await test.Students.MoveStudentAsync(student.Id, groupC.Id);

        var memberships = await test.Db.GroupMemberships.AsNoTracking()
            .Where(x => x.StudentId == student.Id).OrderBy(x => x.From).ToListAsync();
        Assert.Equal(2, memberships.Count);
        Assert.Equal(groupA.Id, memberships[0].GroupId);
        Assert.Equal(DateOnly.MinValue, memberships[0].From);
        Assert.Equal(today.AddDays(-1), memberships[0].To);
        Assert.Equal(groupC.Id, memberships[1].GroupId);
        Assert.Equal(today, memberships[1].From);
        Assert.Null(memberships[1].To);
        Assert.Equal(groupC.Id, (await test.Db.Students.FindAsync(student.Id))!.GroupId);
    }

    [Fact]
    public async Task NewStudentsReceiveInitialMembershipFromServiceAndTestDb()
    {
        using var test = new TestDb();
        var group = test.AddGroup("Группа А", "Анна Иванова");
        var fromTestDb = Assert.Single(group.Students);
        var fromService = await test.Students.AddStudentAsync(group.Id, "Борис Петров");

        foreach (var student in new[] { fromTestDb, fromService })
        {
            var membership = Assert.Single(await test.Db.GroupMemberships.AsNoTracking()
                .Where(x => x.StudentId == student.Id).ToListAsync());
            Assert.Equal(group.Id, membership.GroupId);
            Assert.Equal(DateOnly.MinValue, membership.From);
            Assert.Null(membership.To);
        }
    }

    [Fact]
    public async Task FillFinalIgnoresScoresAfterStudentLeftGroup()
    {
        using var test = new TestDb();
        var teacher = test.AddTeacher();
        var groupA = test.AddGroup("Группа А", "Анна Иванова");
        var groupB = test.AddGroup("Группа Б");
        var student = Assert.Single(groupA.Students);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var yesterday = today.AddDays(-1);
        var yesterdaySlot = test.AddSlot(teacher, groupA, yesterday.DayOfWeek, validFrom: yesterday, oneOffDate: yesterday);
        var todaySlot = test.AddSlot(teacher, groupA, today.DayOfWeek, validFrom: today, oneOffDate: today);
        await test.Attendance.SetLessonGradeAsync(yesterdaySlot.Id, yesterday, student.Id, 100);
        await test.Attendance.SetLessonGradeAsync(todaySlot.Id, today, student.Id, 20);

        await test.Students.MoveStudentAsync(student.Id, groupB.Id);

        Assert.Equal(1, await test.Grades.FillFinalAsync(teacher.Id, groupA.Id, "Алгебра"));
        var final = await test.Db.FinalGrades.AsNoTracking().SingleAsync(x => x.StudentId == student.Id);
        Assert.Equal(100, final.Score);
    }
}
