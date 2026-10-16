using System.Globalization;
using System.Security.Claims;
using System.Text;
using BiStudent.Contracts;
using BiStudent.Server.Data;
using BiStudent.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BiStudent.Server.Endpoints;

public static class ApiEndpoints
{
    public static void MapApi(this WebApplication app)
    {
        app.MapAuth();

        var api = app.MapGroup("/api").RequireAuthorization().AddEndpointFilter(HandleErrorsAsync);
        api.MapGet("/settings/automation", async (IConfiguration configuration, RecognitionService recognition) =>
            Automation(configuration, (await recognition.GetSettingsAsync()).FramesInRow));
        api.MapGet("/settings/recognition", (RecognitionService recognition) => recognition.GetSettingsAsync());
        api.MapPut("/settings/recognition", (RecognitionSettingsDto settings, RecognitionService recognition) => recognition.SaveSettingsAsync(settings))
            .RequireAuthorization("Admin");

        api.MapGrades();
        api.MapGet("/dashboard", async (ClaimsPrincipal user, StatsService stats, ScheduleService schedule) =>
            await DashboardAsync(TeacherId(user), stats, schedule, DateTime.Now));
        api.MapGet("/report", ReportAsync);

        var schedule = api.MapGroup("/schedule");
        schedule.MapGet("/day", async (DateOnly date, ClaimsPrincipal user, ScheduleService service) =>
            (await service.ForDayAsync(TeacherId(user), date)).Select(Map).ToList());
        schedule.MapGet("/week", async (DateOnly monday, ClaimsPrincipal user, ScheduleService service) =>
            (await service.ForWeekAsync(TeacherId(user), monday)).Select(Map).ToList());
        schedule.MapGet("/parity", (DateOnly date, ScheduleService service) => service.WeekParityOf(date));
        schedule.MapPost("/slots", SaveSlotAsync);
        schedule.MapPut("/slots/{id:int}", SaveSlotAsync);
        schedule.MapDelete("/slots/{id:int}", async (int id, ClaimsPrincipal user, AppDbContext db, ScheduleService service) =>
        {
            await EnsureSlotAsync(db, user, id);
            await service.DeleteSlotAsync(id);
            return Results.NoContent();
        });
        schedule.MapPost("/slots/{id:int}/cancel", async (int id, CancelLessonRequest request, ClaimsPrincipal user, AppDbContext db, ScheduleService service) =>
        {
            await EnsureSlotAsync(db, user, id);
            await service.CancelAsync(id, request.Date, string.IsNullOrWhiteSpace(request.Reason) ? "Отменена преподавателем" : request.Reason.Trim());
            return Results.NoContent();
        });
        schedule.MapPost("/slots/{id:int}/restore", async (int id, RestoreLessonRequest request, ClaimsPrincipal user, AppDbContext db, ScheduleService service) =>
        {
            await EnsureSlotAsync(db, user, id);
            await service.RestoreAsync(id, request.Date);
            return Results.NoContent();
        });
        schedule.MapPost("/import", async (HttpRequest request, ClaimsPrincipal user, ScheduleService service) =>
        {
            using var body = new MemoryStream();
            await request.Body.CopyToAsync(body);
            body.Position = 0;
            try { return Results.Ok(new ImportResultDto(await service.ImportCsvAsync(TeacherId(user), body), 0, [])); }
            catch (InvalidOperationException ex)
            {
                var errors = ex.Message.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).ToList();
                return Results.Ok(new ImportResultDto(0, errors.Count, errors));
            }
        });

        var groups = api.MapGroup("/groups");
        groups.MapGet("", async (StudentService students) =>
            (await students.GroupsAsync()).Select(g => new GroupDto(g.Id, g.Name, g.Students, g.WithFace)).ToList());
        groups.MapPost("", async (CreateGroupRequest request, StudentService students) =>
        {
            var group = await students.AddGroupAsync(request.Name);
            return new GroupDto(group.Id, group.Name, 0, 0);
        });
        groups.MapDelete("/{id:int}", async (int id, StudentService students) =>
        {
            await students.DeleteGroupAsync(id);
            return Results.NoContent();
        });
        groups.MapGet("/{id:int}/students", async (int id, StudentService students, FaceSampleHealth health, AppDbContext db) =>
        {
            var faces = await students.WithFaceAsync(id);
            var unreadable = await health.UnreadableStudentsAsync();
            var pending = await db.FaceSubmissions.AsNoTracking()
                .Where(x => x.Student.GroupId == id && x.Status == FaceSubmissionStatus.Pending)
                .Select(x => x.StudentId).Distinct().ToListAsync();
            return (await students.ForGroupAsync(id))
                .Select(s => new StudentDto(s.Id, s.FullName, s.GroupId, faces.Contains(s.Id) && !unreadable.Contains(s.Id),
                    unreadable.Contains(s.Id), pending.Contains(s.Id))).ToList();
        });

        var students = api.MapGroup("/students");
        students.MapPost("", async (SaveStudentRequest request, StudentService service) =>
        {
            var student = await service.AddStudentAsync(request.GroupId, request.FullName);
            return new StudentDto(student.Id, student.FullName, student.GroupId, false);
        });
        students.MapPut("/{id:int}", async (int id, SaveStudentRequest request, AppDbContext db, StudentService service) =>
        {
            var student = await db.Students.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id) ?? throw new KeyNotFoundException("Студент не найден.");
            if (student.FullName != request.FullName.Trim()) await service.RenameStudentAsync(id, request.FullName);
            if (student.GroupId != request.GroupId) await service.MoveStudentAsync(id, request.GroupId);
            return Results.NoContent();
        });
        students.MapDelete("/{id:int}", async (int id, StudentService service) =>
        {
            await service.DeleteStudentAsync(id);
            return Results.NoContent();
        });
        students.MapPost("/import", async (HttpRequest request, StudentService service) =>
        {
            using var body = new MemoryStream();
            await request.Body.CopyToAsync(body);
            body.Position = 0;
            var (added, skipped) = await service.ImportCsvAsync(body);
            return new ImportResultDto(added, skipped, []);
        });
        students.MapPost("/{id:int}/enroll", async (int id, EnrollRequest request, RecognitionService recognition) =>
            await recognition.EnrollAsync(id, request.Images, request.AllowSimilar)).WithMetadata(new RequestSizeLimitAttribute(8 * 1024 * 1024));

        var lessons = api.MapGroup("/lessons/{slotId:int}/{date}");
        lessons.MapGet("/attendance", async (int slotId, DateOnly date, ClaimsPrincipal user, AppDbContext db, AttendanceService attendance) =>
        {
            await EnsureSlotAsync(db, user, slotId);
            return (await attendance.ForLessonAsync(slotId, date))
                .Select(r => new AttendanceRowDto(r.StudentId, r.FullName, r.Status, r.Method, r.MarkedAt,
                    r.Reason, r.Comment, r.HasDocument, r.Score)).ToList();
        });
        lessons.MapPut("/attendance", async (int slotId, DateOnly date, MarkRequest request, ClaimsPrincipal user, AppDbContext db, AttendanceService attendance) =>
        {
            await EnsureSlotAsync(db, user, slotId);
            if (request.Status is AttendanceStatus status) await attendance.MarkAsync(slotId, date, request.StudentId,
                status, MarkMethod.Manual, request.Reason, request.Comment, request.HasDocument);
            else await attendance.ClearAsync(slotId, date, request.StudentId);
            return Results.NoContent();
        });
        lessons.MapPost("/finish", async (int slotId, DateOnly date, ClaimsPrincipal user, AppDbContext db, AttendanceService attendance) =>
        {
            await EnsureSlotAsync(db, user, slotId);
            return new CountResult(await attendance.MarkRestAbsentAsync(slotId, date));
        });
        lessons.MapPost("/all-present", async (int slotId, DateOnly date, ClaimsPrincipal user, AppDbContext db, AttendanceService attendance) =>
        {
            await EnsureSlotAsync(db, user, slotId);
            var count = 0;
            foreach (var row in (await attendance.ForLessonAsync(slotId, date)).Where(r => r.Status is null))
                if (await attendance.MarkAsync(slotId, date, row.StudentId, AttendanceStatus.Present, MarkMethod.Manual)) count++;
            return new CountResult(count);
        });

        api.MapPost("/recognize", async (RecognizeRequest request, ClaimsPrincipal user, AppDbContext db, RecognitionService recognition) =>
        {
            await EnsureSlotAsync(db, user, request.SlotId);
            return await recognition.RecognizeAsync(TeacherId(user), request.SlotId, request.Date, request.Image);
        }).WithMetadata(new RequestSizeLimitAttribute(4 * 1024 * 1024));
        api.MapGet("/recognition/status", async (RecognitionService recognition, FaceSampleHealth health) =>
        {
            if (!recognition.IsAvailable) return new EnrollResultDto(false, recognition.Error ?? "Распознавание недоступно.");
            var unreadable = (await health.UnreadableStudentsAsync()).Count;
            return new EnrollResultDto(true, unreadable == 0 ? "Распознавание готово."
                : $"Образцы лиц {unreadable} студентов не расшифровываются (база перенесена без ключей шифрования). Зарегистрируйте их лица заново — история посещаемости сохранена.");
        });

        var admin = api.MapGroup("/admin").RequireAuthorization("Admin");
        admin.MapGet("/backups", (BackupService backups) => backups.List());
        admin.MapPost("/backups", async (BackupService backups, CancellationToken ct) => await backups.CreateAsync(automatic: false, ct));
        admin.MapGet("/backups/{name}", (string name, BackupService backups) =>
            backups.Resolve(name) is { } path
                ? Results.File(path, "application/zip", Path.GetFileName(path))
                : Results.Json(new ErrorDto("Копия не найдена."), statusCode: StatusCodes.Status404NotFound));
        admin.MapPost("/restore", async (HttpRequest request, ClaimsPrincipal user, BackupService backups, SessionGeneration sessions, CancellationToken ct) =>
            await backups.RestoreAsync(request.Body, ct, () => sessions.IsCurrent(user))).WithMetadata(new RequestSizeLimitAttribute(200L * 1024 * 1024));

        var journal = api.MapGroup("/journal");
        journal.MapGet("/{groupId:int}", async (int groupId, DateOnly from, DateOnly to, string? subject, ClaimsPrincipal user, AttendanceService attendance) =>
            Map(await attendance.JournalAsync(TeacherId(user), groupId, from, to, NullIfEmpty(subject))));
        journal.MapGet("/{groupId:int}/xlsx", async (int groupId, DateOnly from, DateOnly to, string? subject, ClaimsPrincipal user, AttendanceService attendance, ExportService export) =>
        {
            var matrix = await attendance.JournalAsync(TeacherId(user), groupId, from, to, NullIfEmpty(subject));
            return Results.File(export.JournalXlsx(matrix, "Журнал учёта посещаемости и успеваемости"),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", FileName(matrix, ".xlsx"));
        });
        journal.MapGet("/{groupId:int}/csv", async (int groupId, DateOnly from, DateOnly to, string? subject, ClaimsPrincipal user, AttendanceService attendance, ExportService export) =>
        {
            var matrix = await attendance.JournalAsync(TeacherId(user), groupId, from, to, NullIfEmpty(subject));
            return Results.File(Encoding.UTF8.GetBytes(export.JournalCsv(matrix)), "text/csv; charset=utf-8", FileName(matrix, ".csv"));
        });
    }

    internal static async ValueTask<object?> HandleErrorsAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try { return await next(context); }
        catch (InvalidOperationException ex) { return Results.Json(new ErrorDto(ex.Message), statusCode: StatusCodes.Status400BadRequest); }
        catch (KeyNotFoundException ex) { return Results.Json(new ErrorDto(ex.Message), statusCode: StatusCodes.Status404NotFound); }
        catch (UnauthorizedAccessException ex) { return Results.Json(new ErrorDto(ex.Message), statusCode: StatusCodes.Status403Forbidden); }
        catch (FormatException ex) { return Results.Json(new ErrorDto(ex.Message), statusCode: StatusCodes.Status400BadRequest); }
    }

    internal static int TeacherId(ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), CultureInfo.InvariantCulture, out var id)
            ? id : throw new UnauthorizedAccessException("Требуется вход преподавателя.");

    internal static async Task EnsureSlotAsync(AppDbContext db, ClaimsPrincipal user, int slotId)
    {
        var teacherId = TeacherId(user);
        if (!await db.ScheduleSlots.AnyAsync(x => x.Id == slotId && x.TeacherId == teacherId))
            throw new KeyNotFoundException("Пара не найдена в вашем расписании.");
    }

    private static async Task<IResult> SaveSlotAsync(int? id, SaveSlotRequest request, ClaimsPrincipal user,
        AppDbContext db, ScheduleService service, StudentService students, IConfiguration configuration)
    {
        var teacherId = TeacherId(user);
        if (string.IsNullOrWhiteSpace(request.Subject)) throw new InvalidOperationException("Укажите предмет.");
        var groupId = request.GroupId;
        if (!string.IsNullOrWhiteSpace(request.NewGroupName)) groupId = (await students.AddGroupAsync(request.NewGroupName)).Id;
        if (!await db.Groups.AnyAsync(x => x.Id == groupId)) throw new InvalidOperationException("Выберите группу.");
        var existing = id is int slotId ? await db.ScheduleSlots.AsNoTracking().SingleOrDefaultAsync(x => x.Id == slotId && x.TeacherId == teacherId)
            ?? throw new KeyNotFoundException("Пара не найдена в вашем расписании.") : null;
        var semester = DateOnly.Parse(configuration["Semester:Start"] ?? "2026-09-01", CultureInfo.InvariantCulture);
        var slot = new ScheduleSlot
        {
            Id = existing?.Id ?? 0, TeacherId = teacherId, GroupId = groupId, Subject = request.Subject.Trim(),
            Room = string.IsNullOrWhiteSpace(request.Room) ? null : request.Room.Trim(),
            DayOfWeek = request.OneOffDate?.DayOfWeek ?? request.DayOfWeek, Start = request.Start, End = request.End,
            Parity = request.OneOffDate is null ? request.Parity : WeekParity.Every,
            ValidFrom = request.OneOffDate ?? existing?.ValidFrom ?? semester,
            ValidTo = request.OneOffDate ?? existing?.ValidTo, OneOffDate = request.OneOffDate
        };
        if (existing is null) slot = await service.AddSlotAsync(slot);
        else await service.UpdateSlotAsync(slot);
        var group = await db.Groups.AsNoTracking().SingleAsync(x => x.Id == slot.GroupId);
        return Results.Ok(Map(slot, group.Name));
    }

    private static async Task<DashboardDto> DashboardAsync(int teacherId, StatsService stats, ScheduleService schedule, DateTime now)
    {
        var dashboard = await stats.DashboardAsync(teacherId, now);
        var today = await schedule.ForDayAsync(teacherId, DateOnly.FromDateTime(now));
        var current = await schedule.CurrentAsync(teacherId, now);
        var next = current is null ? today.FirstOrDefault(x => !x.Cancelled && x.StartsAt > now) : null;
        return new DashboardDto(
            dashboard.LessonsToday, current is null ? null : Map(current), next is null ? null : Map(next), today.Select(Map).ToList(),
            dashboard.Attendance7Days, dashboard.Attendance30Days,
            dashboard.Groups.Select(g => new GroupAttendanceDto(g.GroupId, g.GroupName, g.Percent)).ToList(),
            dashboard.AtRisk.Select(r => new AtRiskStudentDto(r.StudentId, r.FullName, r.GroupName, r.Percent, r.Reason)).ToList(),
            dashboard.CameraMarks7Days, dashboard.CameraMarks30Days, dashboard.SavedMinutes7Days, dashboard.SavedMinutes30Days,
            dashboard.SavingsFormula, dashboard.Trend.Select(t => new TrendPointDto(t.Date, t.Percent)).ToList(),
            dashboard.AverageScore30Days, dashboard.PendingFaceSubmissions);
    }

    private static async Task<ReportDto> ReportAsync(ClaimsPrincipal user, StatsService stats, ScheduleService schedule,
        StudentService students, IConfiguration configuration)
    {
        var teacherId = TeacherId(user);
        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);
        var dashboard = await DashboardAsync(teacherId, stats, schedule, now);
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var week = await schedule.ForWeekAsync(teacherId, monday);
        var groupIds = week.Select(x => x.Slot.GroupId).ToHashSet();
        var groups = (await students.GroupsAsync()).Where(g => groupIds.Contains(g.Id)).ToList();
        var automation = Automation(configuration);
        return new ReportDto(dashboard, await stats.ReportsExportedAsync(today.AddDays(-29), today),
            week.Count(x => !x.Cancelled), groups.Count, groups.Count == 0 ? 25 : (int)Math.Round(groups.Average(g => g.Students)),
            automation.SecondsPerManualMark, automation.MinutesPerManualReport);
    }

    private static AutomationSettingsDto Automation(IConfiguration configuration, int? framesInRow = null) => new(
        configuration.GetValue("Automation:SecondsPerManualMark", 6), configuration.GetValue("Automation:MinutesPerManualReport", 20),
        framesInRow ?? configuration.GetValue("Recognition:FramesInRow", 3));

    private static SlotDto Map(ScheduleSlot slot, string groupName) => new(
        slot.Id, slot.GroupId, groupName, slot.Subject, slot.Room, slot.DayOfWeek, slot.Start, slot.End,
        slot.Parity, slot.ValidFrom, slot.ValidTo, slot.OneOffDate);

    private static LessonDto Map(LessonOccurrence lesson) =>
        new(Map(lesson.Slot, lesson.GroupName), lesson.Date, lesson.StartsAt, lesson.EndsAt, lesson.Cancelled);

    private static JournalDto Map(JournalMatrix matrix) => new(matrix.TeacherName, matrix.GroupName, matrix.Subject,
        matrix.From, matrix.To, matrix.HoursPerLesson,
        matrix.Columns.Select(c => new JournalColumnDto(c.Kind, c.Id, c.Date, c.Start, c.Subject, c.Title, c.AssessmentKind)).ToList(),
        matrix.Students.Select(s => new JournalStudentDto(s.StudentId, s.FullName,
            s.Cells.Select(c => new JournalCellDto(c.Status, c.Reason, c.Comment, c.Score)).ToList(),
            s.Present, s.Absent, s.Excused, s.MissedHours, s.ExcusedHours, s.Percent, s.Average, s.Final)).ToList());

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string FileName(JournalMatrix matrix, string extension)
    {
        var group = string.Concat(matrix.GroupName.Where(x => !Path.GetInvalidFileNameChars().Contains(x)));
        return $"Журнал_{group}_{matrix.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}_{matrix.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}{extension}";
    }
}
