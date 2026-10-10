using BiStudent.Contracts;
using System.Globalization;
using BiStudent.Server.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace BiStudent.Server.Services;

public sealed class SeedService(AppDbContext db, IConfiguration configuration, IWebHostEnvironment environment,
    IPasswordHasher<Teacher> hasher, ScheduleService schedule)
{
    public async Task SeedAsync()
    {
        if (await db.Teachers.AnyAsync()) return;
        var teacher = new Teacher {
            Login = configuration["Demo:Login"] ?? "teacher",
            FullName = configuration["Demo:TeacherName"] ?? "Преподаватель (демо)"
        };
        teacher.PasswordHash = hasher.HashPassword(teacher, configuration["Demo:Password"] ?? "teacher123");
        db.Teachers.Add(teacher);
        await db.SaveChangesAsync();

        var path = new[] {
            Path.Combine(environment.ContentRootPath, "seed", "students.local.csv"),
            Path.Combine(AppContext.BaseDirectory, "seed", "students.local.csv"),
            Path.Combine(environment.ContentRootPath, "seed", "students.demo.csv"),
            Path.Combine(AppContext.BaseDirectory, "seed", "students.demo.csv")
        }.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("Не найден список студентов для демо.");
        foreach (var raw in await File.ReadAllLinesAsync(path))
        {
            var parts = raw.Split(';').Select(x => x.Trim()).ToArray();
            if (parts.Length < 2 || parts[0].Equals("ФИО", StringComparison.OrdinalIgnoreCase)) continue;
            var group = await db.Groups.SingleOrDefaultAsync(x => x.Name == parts[1]);
            if (group is null) { group = new Group { Name = parts[1] }; db.Groups.Add(group); await db.SaveChangesAsync(); }
            if (!await db.Students.AnyAsync(x => x.GroupId == group.Id && x.FullName == parts[0]))
                db.Students.Add(new Student { FullName = parts[0], GroupId = group.Id });
        }
        await db.SaveChangesAsync();

        var start = DateOnly.Parse(configuration["Semester:Start"] ?? "2026-09-01", CultureInfo.InvariantCulture);
        var subjects = new[] { "Базы данных", "DevOps", "Веб-программирование", "Алгоритмы", "Английский язык", "Компьютерные сети" };
        var bells = new[] { (new TimeOnly(8, 30), new TimeOnly(9, 50)), (new TimeOnly(10, 0), new TimeOnly(11, 20)),
            (new TimeOnly(11, 30), new TimeOnly(12, 50)), (new TimeOnly(13, 20), new TimeOnly(14, 40)),
            (new TimeOnly(14, 50), new TimeOnly(16, 10)), (new TimeOnly(16, 20), new TimeOnly(17, 40)) };
        var groups = await db.Groups.OrderByDescending(x => x.Name).ToListAsync();
        for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            for (var day = 0; day < 5; day++)
            {
                foreach (var pair in new[] { groupIndex * 2 % 6, (groupIndex * 2 + 3) % 6 })
                {
                    var subject = subjects[(day * 2 + pair + groupIndex) % subjects.Length];
                    db.ScheduleSlots.Add(new ScheduleSlot {
                        TeacherId = teacher.Id, GroupId = groups[groupIndex].Id,
                        Subject = subject, Room = $"{305 + groupIndex * 4 + pair % 2}",
                        DayOfWeek = (DayOfWeek)(day + 1), Start = bells[pair].Item1, End = bells[pair].Item2,
                        Parity = day == 4 && pair > 2 ? (groupIndex % 2 == 0 ? WeekParity.Odd : WeekParity.Even) : WeekParity.Every,
                        ValidFrom = start
                    });
                }
            }
        }
        await db.SaveChangesAsync();
        var yesterday = DateOnly.FromDateTime(DateTime.Now).AddDays(-1);
        var first = yesterday.AddDays(-27);
        if (first < start) first = start;
        var past = new List<LessonOccurrence>();
        for (var day = first; day <= yesterday; day = day.AddDays(1))
            past.AddRange(await schedule.ForDayAsync(teacher.Id, day));
        var cancelled = past.FirstOrDefault();
        if (cancelled is not null)
        {
            db.SlotCancellations.Add(new SlotCancellation {
                SlotId = cancelled.Slot.Id, Date = cancelled.Date, Reason = "Демо: занятие отменено"
            });
        }
        var students = await db.Students.OrderBy(x => x.Id).ToListAsync();
        var riskIds = students.Take(3).Select(x => x.Id).ToHashSet();
        var random = new Random(42);
        foreach (var lesson in past.Where(x => x != cancelled))
        {
            foreach (var student in students.Where(x => x.GroupId == lesson.Slot.GroupId))
            {
                var roll = random.NextDouble();
                var risk = riskIds.Contains(student.Id);
                var status = roll < (risk ? 0.60 : 0.89) ? AttendanceStatus.Present :
                    roll < (risk ? 0.65 : 0.94) ? AttendanceStatus.Late :
                    roll < (risk ? 0.68 : 0.97) ? AttendanceStatus.Excused : AttendanceStatus.Absent;
                var attended = status is AttendanceStatus.Present or AttendanceStatus.Late;
                db.AttendanceMarks.Add(new AttendanceMark {
                    SlotId = lesson.Slot.Id, Date = lesson.Date, StudentId = student.Id, Status = status,
                    Method = attended && random.NextDouble() < 0.93 ? MarkMethod.Camera : MarkMethod.Manual,
                    MarkedAt = attended
                        ? lesson.StartsAt.AddMinutes(status == AttendanceStatus.Late ? 12 + random.Next(15) : random.Next(-4, 6)).AddSeconds(random.Next(60))
                        : lesson.EndsAt.AddMinutes(-random.Next(5))
                });
            }
        }
        for (var week = 0; week < 4; week++)
        {
            var exported = yesterday.AddDays(-7 * week);
            if (exported < start) break;
            db.AppSettings.Add(new AppSetting {
                Key = $"stats.reports_exported.{exported.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}", Value = "2"
            });
        }
        await db.SaveChangesAsync();
    }
}
