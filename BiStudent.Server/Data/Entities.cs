using BiStudent.Contracts;

namespace BiStudent.Server.Data;

public sealed class Teacher
{
    public int Id { get; set; }
    public string Login { get; set; } = "";
    public string FullName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
}

public sealed class Group
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<Student> Students { get; set; } = [];
}

public sealed class Student
{
    public int Id { get; set; }
    public string FullName { get; set; } = "";
    public int GroupId { get; set; }
    public Group Group { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public List<FaceSample> Samples { get; set; } = [];
}

public sealed class FaceSample
{
    public int Id { get; set; }
    public int StudentId { get; set; }
    public Student Student { get; set; } = null!;
    public byte[] ProtectedImage { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

public sealed class ScheduleSlot
{
    public int Id { get; set; }
    public int TeacherId { get; set; }
    public Teacher Teacher { get; set; } = null!;
    public int GroupId { get; set; }
    public Group Group { get; set; } = null!;
    public string Subject { get; set; } = "";
    public string? Room { get; set; }
    public DayOfWeek DayOfWeek { get; set; }
    public TimeOnly Start { get; set; }
    public TimeOnly End { get; set; }
    public WeekParity Parity { get; set; }
    public DateOnly ValidFrom { get; set; }
    public DateOnly? ValidTo { get; set; }
    public DateOnly? OneOffDate { get; set; }
    public List<SlotCancellation> Cancellations { get; set; } = [];
}

public sealed class SlotCancellation
{
    public int Id { get; set; }
    public int SlotId { get; set; }
    public ScheduleSlot Slot { get; set; } = null!;
    public DateOnly Date { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class AttendanceMark
{
    public int Id { get; set; }
    public int SlotId { get; set; }
    public ScheduleSlot Slot { get; set; } = null!;
    public DateOnly Date { get; set; }
    public int StudentId { get; set; }
    public Student Student { get; set; } = null!;
    public AttendanceStatus Status { get; set; }
    public MarkMethod Method { get; set; }
    public DateTime MarkedAt { get; set; }
}

public sealed class AppSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}
