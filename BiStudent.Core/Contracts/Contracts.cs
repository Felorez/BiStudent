namespace BiStudent.Contracts;

public enum WeekParity { Every, Odd, Even }
public enum AttendanceStatus { Present, Late, Absent, Excused }
public enum MarkMethod { Camera, Manual, Demo }

public sealed record LoginRequest(string Login, string Password);
public sealed record TokenResponse(string AccessToken, long ExpiresIn);
public sealed record TeacherDto(int Id, string FullName, string Login);

public sealed record GroupDto(int Id, string Name, int Students, int WithFace);
public sealed record StudentDto(int Id, string FullName, int GroupId, bool HasFace);
public sealed record CreateGroupRequest(string Name);
public sealed record SaveStudentRequest(string FullName, int GroupId);

public sealed record SlotDto(
    int Id, int GroupId, string GroupName, string Subject, string? Room, DayOfWeek DayOfWeek,
    TimeOnly Start, TimeOnly End, WeekParity Parity, DateOnly ValidFrom, DateOnly? ValidTo, DateOnly? OneOffDate);

public sealed record SaveSlotRequest(
    int GroupId, string? NewGroupName, string Subject, string? Room, DayOfWeek DayOfWeek,
    TimeOnly Start, TimeOnly End, WeekParity Parity, DateOnly? OneOffDate);

public sealed record LessonDto(SlotDto Slot, DateOnly Date, DateTime StartsAt, DateTime EndsAt, bool Cancelled);
public sealed record CancelLessonRequest(DateOnly Date, string? Reason);
public sealed record RestoreLessonRequest(DateOnly Date);

public sealed record AttendanceRowDto(int StudentId, string FullName, AttendanceStatus? Status, MarkMethod? Method, DateTime? MarkedAt);
public sealed record MarkRequest(int StudentId, AttendanceStatus? Status);
public sealed record CountResult(int Count);

public sealed record JournalColumnDto(int SlotId, DateOnly Date, TimeOnly Start, string Subject);
public sealed record JournalStudentDto(int StudentId, string FullName, List<AttendanceStatus?> Statuses, double Percent);
public sealed record JournalDto(string TeacherName, string GroupName, DateOnly From, DateOnly To,
    List<JournalColumnDto> Columns, List<JournalStudentDto> Students);

public sealed record GroupAttendanceDto(int GroupId, string GroupName, double Percent);
public sealed record AtRiskStudentDto(int StudentId, string FullName, string GroupName, double Percent, string Reason);
public sealed record TrendPointDto(DateOnly Date, double Percent);
public sealed record DashboardDto(
    int LessonsToday, LessonDto? Current, LessonDto? Next, List<LessonDto> Today,
    double Attendance7Days, double Attendance30Days, List<GroupAttendanceDto> Groups, List<AtRiskStudentDto> AtRisk,
    int CameraMarks7Days, int CameraMarks30Days, double SavedMinutes7Days, double SavedMinutes30Days,
    string SavingsFormula, List<TrendPointDto> Trend);

public sealed record ReportDto(
    DashboardDto Dashboard, int ReportsExported30Days, int LessonsPerWeek, int GroupCount, int AverageGroupSize,
    int SecondsPerManualMark, int MinutesPerManualReport);

public sealed record AutomationSettingsDto(int SecondsPerManualMark, int MinutesPerManualReport, int LateAfterMinutes, int FramesInRow);

public sealed record RecognizeRequest(int SlotId, DateOnly Date, byte[] Image);
public sealed record RecognizedFaceDto(int X, int Y, int W, int H, int? StudentId, string? Name, double Similarity,
    bool Marked, bool AlreadyMarked, bool OtherGroup);
public sealed record RecognitionResultDto(List<RecognizedFaceDto> Faces, int FrameWidth, int FrameHeight, string? Error = null);

public sealed record EnrollRequest(List<byte[]> Images);
public sealed record EnrollResultDto(bool Ok, string Message);

public sealed record ImportResultDto(int Added, int Skipped, List<string> Errors);
public sealed record ErrorDto(string Message);
