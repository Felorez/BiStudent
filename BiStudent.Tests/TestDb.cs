using BiStudent.Contracts;
using BiStudent.Server.Data;
using BiStudent.Server.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BiStudent.Tests;

internal sealed class TestDb : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly ServiceProvider services = new ServiceCollection().BuildServiceProvider();
    private readonly RecognitionService recognition;

    public AppDbContext Db { get; }
    public IConfiguration Configuration { get; }
    public ScheduleService Schedule { get; }
    public AttendanceNotifier Notifier { get; } = new();
    public AttendanceService Attendance { get; }
    public StatsService Stats { get; }
    public StudentService Students { get; }
    public ExportService Export { get; }

    public GradeService Grades { get; }

    public TestDb()
    {
        connection.Open();
        Db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        DatabaseInitializer.Initialize(Db);
        Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Semester:Start"] = "2026-09-01",
            ["Attendance:HoursPerLesson"] = "2",
            ["Automation:SecondsPerManualMark"] = "6",
            ["Automation:MinutesPerManualReport"] = "20"
        }).Build();
        Schedule = new ScheduleService(Db, Configuration);
        Attendance = new AttendanceService(Db, Configuration, Schedule, Notifier);
        Stats = new StatsService(Db, Schedule, Configuration);
        recognition = new RecognitionService(services.GetRequiredService<IServiceScopeFactory>(),
            DataProtectionProvider.Create("tests"), Configuration);
        Students = new StudentService(Db, recognition);
        Export = new ExportService(Db);
        Grades = new GradeService(Db, Configuration);
    }

    public Teacher AddTeacher()
    {
        var teacher = new Teacher { Login = "teacher", FullName = "Ирина Иванова" };
        Db.Teachers.Add(teacher);
        Db.SaveChanges();
        return teacher;
    }

    public Group AddGroup(string name, params string[] studentNames)
    {
        var group = new Group { Name = name, Students = studentNames.Select(x => new Student { FullName = x }).ToList() };
        Db.Groups.Add(group);
        Db.SaveChanges();
        return group;
    }

    public ScheduleSlot NewSlot(Teacher teacher, Group group, DayOfWeek day = DayOfWeek.Tuesday,
        TimeOnly? start = null, TimeOnly? end = null, WeekParity parity = WeekParity.Every,
        DateOnly? validFrom = null, DateOnly? validTo = null, DateOnly? oneOffDate = null, string subject = "Алгебра") => new()
    {
        TeacherId = teacher.Id, GroupId = group.Id, DayOfWeek = day,
        Start = start ?? new TimeOnly(9, 0), End = end ?? new TimeOnly(10, 0),
        Parity = parity, ValidFrom = validFrom ?? new DateOnly(2026, 9, 1),
        ValidTo = validTo, OneOffDate = oneOffDate, Subject = subject
    };

    public ScheduleSlot AddSlot(Teacher teacher, Group group, DayOfWeek day = DayOfWeek.Tuesday,
        TimeOnly? start = null, TimeOnly? end = null, WeekParity parity = WeekParity.Every,
        DateOnly? validFrom = null, DateOnly? validTo = null, DateOnly? oneOffDate = null, string subject = "Алгебра")
    {
        var slot = NewSlot(teacher, group, day, start, end, parity, validFrom, validTo, oneOffDate, subject);
        Db.ScheduleSlots.Add(slot);
        Db.SaveChanges();
        return slot;
    }

    public void Dispose()
    {
        recognition.Dispose();
        Db.Dispose();
        connection.Dispose();
        services.Dispose();
    }
}
