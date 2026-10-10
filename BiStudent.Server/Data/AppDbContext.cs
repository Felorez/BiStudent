using BiStudent.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BiStudent.Server.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Teacher> Teachers => Set<Teacher>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<Student> Students => Set<Student>();
    public DbSet<FaceSample> FaceSamples => Set<FaceSample>();
    public DbSet<ScheduleSlot> ScheduleSlots => Set<ScheduleSlot>();
    public DbSet<SlotCancellation> SlotCancellations => Set<SlotCancellation>();
    public DbSet<AttendanceMark> AttendanceMarks => Set<AttendanceMark>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Teacher>().HasIndex(x => x.Login).IsUnique();
        model.Entity<Group>().HasIndex(x => x.Name).IsUnique();
        model.Entity<AppSetting>().HasKey(x => x.Key);
        model.Entity<SlotCancellation>().HasIndex(x => new { x.SlotId, x.Date }).IsUnique();
        model.Entity<AttendanceMark>().HasIndex(x => new { x.SlotId, x.Date, x.StudentId }).IsUnique();
        model.Entity<Student>().HasOne(x => x.Group).WithMany(x => x.Students).HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<FaceSample>().HasOne(x => x.Student).WithMany(x => x.Samples).HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<ScheduleSlot>().HasOne(x => x.Teacher).WithMany().HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ScheduleSlot>().HasOne(x => x.Group).WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<SlotCancellation>().HasOne(x => x.Slot).WithMany(x => x.Cancellations).HasForeignKey(x => x.SlotId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<AttendanceMark>().HasOne(x => x.Slot).WithMany().HasForeignKey(x => x.SlotId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<AttendanceMark>().HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Cascade);
    }
}
