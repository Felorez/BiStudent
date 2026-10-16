using System.Globalization;
using BiStudent.Contracts;
using BiStudent.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace BiStudent.Server.Services;

public sealed class GradeService(AppDbContext db, IConfiguration configuration)
{
    private static readonly SemaphoreSlim AssessmentGate = new(1, 1);

    public Task<List<string>> SubjectsAsync(int teacherId, int groupId) =>
        db.ScheduleSlots.AsNoTracking().Where(x => x.TeacherId == teacherId && x.GroupId == groupId)
            .Select(x => x.Subject).Distinct().OrderBy(x => x).ToListAsync();

    public async Task<List<AssessmentDto>> ListAsync(int teacherId, int? groupId, string? subject)
    {
        var query = db.Assessments.AsNoTracking().Where(x => x.TeacherId == teacherId);
        if (groupId is not null) query = query.Where(x => x.GroupId == groupId);
        if (!string.IsNullOrWhiteSpace(subject))
        {
            var name = subject.Trim();
            query = query.Where(x => x.Subject == name);
        }
        return await query.OrderBy(x => x.Date).ThenBy(x => x.Id)
            .Select(x => new AssessmentDto(x.Id, x.GroupId, x.Group.Name, x.Subject, x.Title, x.Kind, x.Date)).ToListAsync();
    }

    public async Task<AssessmentDto> SaveAsync(int teacherId, int? id, SaveAssessmentRequest request)
    {
        await AssessmentGate.WaitAsync();
        try { return await SaveCoreAsync(teacherId, id, request); }
        finally { AssessmentGate.Release(); }
    }

    private async Task<AssessmentDto> SaveCoreAsync(int teacherId, int? id, SaveAssessmentRequest request)
    {
        var assessment = id is int assessmentId
            ? await OwnAssessmentAsync(teacherId, assessmentId)
            : new Assessment { TeacherId = teacherId };
        var group = await db.Groups.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.GroupId)
            ?? throw new InvalidOperationException("Группа не найдена.");
        var subject = await ValidateSubjectAsync(teacherId, group.Id, request.Subject);
        var title = request.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title)) throw new InvalidOperationException("Укажите название контрольного мероприятия.");
        if (title.Length > 120) throw new InvalidOperationException("Название длиннее 120 символов.");
        if (!Enum.IsDefined(request.Kind)) throw new InvalidOperationException("Неверный вид контрольного мероприятия.");
        if (id is not null && (assessment.GroupId != group.Id || assessment.Subject != subject) &&
            await db.AssessmentGrades.AnyAsync(x => x.AssessmentId == assessment.Id))
            throw new InvalidOperationException(assessment.GroupId != group.Id
                ? "Нельзя перенести мероприятие с выставленными баллами в другую группу."
                : "Нельзя перенести мероприятие с выставленными баллами на другой предмет.");
        assessment.GroupId = group.Id;
        assessment.Subject = subject;
        assessment.Title = title;
        assessment.Kind = request.Kind;
        assessment.Date = request.Date;
        if (id is null) db.Assessments.Add(assessment);
        await db.SaveChangesAsync();
        return new AssessmentDto(assessment.Id, group.Id, group.Name, subject, title, assessment.Kind, assessment.Date);
    }

    public async Task DeleteAsync(int teacherId, int id)
    {
        db.Assessments.Remove(await OwnAssessmentAsync(teacherId, id));
        await db.SaveChangesAsync();
    }

    public async Task SetGradeAsync(int teacherId, int assessmentId, int studentId, int? score)
    {
        await AssessmentGate.WaitAsync();
        try { await SetGradeCoreAsync(teacherId, assessmentId, studentId, score); }
        finally { AssessmentGate.Release(); }
    }

    private async Task SetGradeCoreAsync(int teacherId, int assessmentId, int studentId, int? score)
    {
        var assessment = await OwnAssessmentAsync(teacherId, assessmentId);
        await ValidateStudentAsync(assessment.GroupId, studentId);
        ValidateScore(score);
        var grade = await db.AssessmentGrades.SingleOrDefaultAsync(x => x.AssessmentId == assessmentId && x.StudentId == studentId);
        if (score is null)
        {
            if (grade is null) return;
            db.AssessmentGrades.Remove(grade);
        }
        else if (grade is null)
            db.AssessmentGrades.Add(new AssessmentGrade { AssessmentId = assessmentId, StudentId = studentId, Score = score.Value });
        else
        {
            if (grade.Score == score) return;
            grade.Score = score.Value;
            grade.UpdatedAt = DateTime.Now;
        }
        await db.SaveChangesAsync();
    }

    public async Task SetFinalAsync(int teacherId, int groupId, string subject, int studentId, int? score)
    {
        subject = await ValidateSubjectAsync(teacherId, groupId, subject);
        await ValidateStudentAsync(groupId, studentId);
        ValidateScore(score);
        var grade = await db.FinalGrades.SingleOrDefaultAsync(x =>
            x.TeacherId == teacherId && x.GroupId == groupId && x.Subject == subject && x.StudentId == studentId);
        if (score is null)
        {
            if (grade is null) return;
            db.FinalGrades.Remove(grade);
        }
        else if (grade is null)
            db.FinalGrades.Add(new FinalGrade {
                TeacherId = teacherId, GroupId = groupId, Subject = subject, StudentId = studentId, Score = score.Value
            });
        else
        {
            if (grade.Score == score) return;
            grade.Score = score.Value;
            grade.UpdatedAt = DateTime.Now;
        }
        await db.SaveChangesAsync();
    }

    public async Task<int> FillFinalAsync(int teacherId, int groupId, string subject)
    {
        subject = await ValidateSubjectAsync(teacherId, groupId, subject);
        var semester = DateOnly.Parse(configuration["Semester:Start"] ?? "2026-09-01", CultureInfo.InvariantCulture);
        var students = await db.Students.AsNoTracking().Where(x => x.GroupId == groupId).Select(x => x.Id).ToListAsync();
        var existing = await db.FinalGrades.AsNoTracking()
            .Where(x => x.TeacherId == teacherId && x.GroupId == groupId && x.Subject == subject)
            .Select(x => x.StudentId).ToListAsync();
        var lessonGrades = await db.LessonGrades.AsNoTracking()
            .Where(x => x.Slot.TeacherId == teacherId && x.Slot.GroupId == groupId &&
                x.Slot.Subject == subject && x.Date >= semester)
            .Select(x => new { x.StudentId, x.Score }).ToListAsync();
        var assessmentGrades = await db.AssessmentGrades.AsNoTracking()
            .Where(x => x.Assessment.TeacherId == teacherId && x.Assessment.GroupId == groupId &&
                x.Assessment.Subject == subject && x.Assessment.Date >= semester)
            .Select(x => new { x.StudentId, x.Score }).ToListAsync();
        var scores = lessonGrades.Concat(assessmentGrades).GroupBy(x => x.StudentId)
            .ToDictionary(x => x.Key, x => x.Select(y => y.Score).ToList());
        var count = 0;
        foreach (var studentId in students)
        {
            if (existing.Contains(studentId) || !scores.TryGetValue(studentId, out var values)) continue;
            db.FinalGrades.Add(new FinalGrade {
                TeacherId = teacherId, GroupId = groupId, Subject = subject, StudentId = studentId,
                Score = Math.Clamp((int)Math.Round(values.Average(), MidpointRounding.AwayFromZero), 1, 100)
            });
            count++;
        }
        if (count > 0) await db.SaveChangesAsync();
        return count;
    }

    private async Task<Assessment> OwnAssessmentAsync(int teacherId, int id) =>
        await db.Assessments.SingleOrDefaultAsync(x => x.Id == id && x.TeacherId == teacherId)
            ?? throw new KeyNotFoundException("Контрольное мероприятие не найдено.");

    private async Task<string> ValidateSubjectAsync(int teacherId, int groupId, string subject)
    {
        var name = subject?.Trim();
        if (string.IsNullOrWhiteSpace(name) || !await db.ScheduleSlots.AnyAsync(x =>
            x.TeacherId == teacherId && x.GroupId == groupId && x.Subject == name))
            throw new InvalidOperationException("Этот предмет не ведётся у группы в вашем расписании.");
        return name;
    }

    private async Task ValidateStudentAsync(int groupId, int studentId)
    {
        if (!await db.Students.AnyAsync(x => x.Id == studentId && x.GroupId == groupId))
            throw new InvalidOperationException("Студент не принадлежит группе.");
    }

    private static void ValidateScore(int? score)
    {
        if (score is < 1 or > 100) throw new InvalidOperationException("Балл должен быть от 1 до 100.");
    }
}
