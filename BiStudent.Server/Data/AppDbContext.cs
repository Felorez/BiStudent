using BiStudent.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BiStudent.Server.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Teacher> Teachers => Set<Teacher>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<Student> Students => Set<Student>();
    public DbSet<GroupMembership> GroupMemberships => Set<GroupMembership>();
    public DbSet<FaceSample> FaceSamples => Set<FaceSample>();
    public DbSet<ScheduleSlot> ScheduleSlots => Set<ScheduleSlot>();
    public DbSet<SlotCancellation> SlotCancellations => Set<SlotCancellation>();
    public DbSet<AttendanceMark> AttendanceMarks => Set<AttendanceMark>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<LessonGrade> LessonGrades => Set<LessonGrade>();
    public DbSet<Assessment> Assessments => Set<Assessment>();
    public DbSet<AssessmentGrade> AssessmentGrades => Set<AssessmentGrade>();
    public DbSet<FinalGrade> FinalGrades => Set<FinalGrade>();
    public DbSet<EnrollmentCode> EnrollmentCodes => Set<EnrollmentCode>();
    public DbSet<FaceSubmission> FaceSubmissions => Set<FaceSubmission>();
    public DbSet<FaceSubmissionImage> FaceSubmissionImages => Set<FaceSubmissionImage>();

    public IQueryable<GroupMembership> MembershipsOn(DateOnly date) =>
        GroupMemberships.Where(x => x.From <= date && (x.To == null || x.To >= date));

    public IQueryable<GroupMembership> MembershipsDuring(DateOnly from, DateOnly to) =>
        GroupMemberships.Where(x => x.From <= to && (x.To == null || x.To >= from));

    private void AddInitialMemberships()
    {
        foreach (var entry in ChangeTracker.Entries<Student>().Where(x => x.State == EntityState.Added))
        {
            var student = entry.Entity;
            if (student.Memberships.Count > 0) continue;
            var membership = new GroupMembership { From = DateOnly.MinValue };
            if (student.GroupId == 0) membership.Group = student.Group;
            else membership.GroupId = student.GroupId;
            student.Memberships.Add(membership);
        }
    }

    public override int SaveChanges() => SaveChanges(true);

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        AddInitialMemberships();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        SaveChangesAsync(true, cancellationToken);

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        AddInitialMemberships();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Teacher>().HasIndex(x => x.Login).IsUnique();
        model.Entity<Group>().HasIndex(x => x.Name).IsUnique();
        model.Entity<AppSetting>().HasKey(x => x.Key);
        model.Entity<SlotCancellation>().HasIndex(x => new { x.SlotId, x.Date }).IsUnique();
        model.Entity<AttendanceMark>().HasIndex(x => new { x.SlotId, x.Date, x.StudentId }).IsUnique();
        model.Entity<Student>().HasOne(x => x.Group).WithMany(x => x.Students).HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<GroupMembership>().HasIndex(x => new { x.StudentId, x.From }).IsUnique();
        model.Entity<GroupMembership>().HasIndex(x => x.GroupId);
        model.Entity<GroupMembership>().HasOne(x => x.Student).WithMany(x => x.Memberships).HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<GroupMembership>().HasOne(x => x.Group).WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<FaceSample>().HasOne(x => x.Student).WithMany(x => x.Samples).HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<ScheduleSlot>().HasOne(x => x.Teacher).WithMany().HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ScheduleSlot>().HasOne(x => x.Group).WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<SlotCancellation>().HasOne(x => x.Slot).WithMany(x => x.Cancellations).HasForeignKey(x => x.SlotId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<AttendanceMark>().HasOne(x => x.Slot).WithMany().HasForeignKey(x => x.SlotId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<AttendanceMark>().HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<AttendanceMark>().Property(x => x.Comment).HasMaxLength(500);

        model.Entity<LessonGrade>().HasIndex(x => new { x.SlotId, x.Date, x.StudentId }).IsUnique();
        model.Entity<LessonGrade>().HasOne(x => x.Slot).WithMany().HasForeignKey(x => x.SlotId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<LessonGrade>().HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<LessonGrade>().ToTable(t => t.HasCheckConstraint("CK_LessonGrades_Score", "Score BETWEEN 1 AND 100"));

        model.Entity<Assessment>().HasIndex(x => new { x.TeacherId, x.GroupId, x.Subject });
        model.Entity<Assessment>().HasOne(x => x.Teacher).WithMany().HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<Assessment>().HasOne(x => x.Group).WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<AssessmentGrade>().HasIndex(x => new { x.AssessmentId, x.StudentId }).IsUnique();
        model.Entity<AssessmentGrade>().HasOne(x => x.Assessment).WithMany(x => x.Grades).HasForeignKey(x => x.AssessmentId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<AssessmentGrade>().HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<AssessmentGrade>().ToTable(t => t.HasCheckConstraint("CK_AssessmentGrades_Score", "Score BETWEEN 1 AND 100"));

        model.Entity<FinalGrade>().HasIndex(x => new { x.TeacherId, x.GroupId, x.Subject, x.StudentId }).IsUnique();
        model.Entity<FinalGrade>().HasOne(x => x.Teacher).WithMany().HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<FinalGrade>().HasOne(x => x.Group).WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<FinalGrade>().HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<FinalGrade>().ToTable(t => t.HasCheckConstraint("CK_FinalGrades_Score", "Score BETWEEN 1 AND 100"));

        model.Entity<EnrollmentCode>().HasIndex(x => x.Code).IsUnique();
        model.Entity<EnrollmentCode>().HasOne(x => x.Group).WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<EnrollmentCode>().HasOne(x => x.CreatedBy).WithMany().HasForeignKey(x => x.CreatedByTeacherId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<FaceSubmission>().HasIndex(x => new { x.Status, x.CreatedAt });
        model.Entity<FaceSubmission>().HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<FaceSubmission>().HasOne(x => x.EnrollmentCode).WithMany().HasForeignKey(x => x.EnrollmentCodeId).OnDelete(DeleteBehavior.SetNull);
        model.Entity<FaceSubmissionImage>().HasOne(x => x.Submission).WithMany(x => x.Images).HasForeignKey(x => x.SubmissionId).OnDelete(DeleteBehavior.Cascade);
    }
}
