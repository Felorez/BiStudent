using BiStudent.Server.Data;
using BiStudent.Server.Sync;
using Xunit;

namespace BiStudent.Tests;

public sealed class SyncModelTests
{
    [Fact]
    public void TablesFollowForeignKeysAndExcludeLocalData()
    {
        using var database = new TestDb();
        var tables = new SyncModel(database.Db).Tables;
        var names = tables.Select(table => table.Name).ToArray();

        Assert.Contains("Groups", names);
        Assert.Contains("Students", names);
        Assert.Contains("Teachers", names);
        Assert.Contains("ScheduleSlots", names);
        Assert.Contains("AttendanceMarks", names);
        Assert.DoesNotContain("AppSettings", names);
        Assert.DoesNotContain("SyncLinks", names);
        Assert.DoesNotContain("SyncOutbox", names);

        Assert.True(Array.IndexOf(names, "Groups") < Array.IndexOf(names, "Students"));
        Assert.True(Array.IndexOf(names, "Teachers") < Array.IndexOf(names, "ScheduleSlots"));
        Assert.True(Array.IndexOf(names, "ScheduleSlots") <
            Array.IndexOf(names, "AttendanceMarks"));
        Assert.DoesNotContain(tables.Single(table => table.ClrType == typeof(Teacher))
            .Properties, property => property.Name == nameof(Teacher.PasswordHash));
    }
}
