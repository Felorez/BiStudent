using System.Drawing;
using System.Globalization;
using BiStudent.Contracts;

namespace BiStudent;

internal static class UiFormat
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly Color Green = Color.FromArgb(0x4A, 0xD8, 0x9A);
    private static readonly Color Yellow = Color.FromArgb(0xF5, 0xC4, 0x51);
    private static readonly Color Red = Color.FromArgb(0xF2, 0x78, 0x78);
    private static readonly Color Violet = Color.FromArgb(0xA7, 0x8B, 0xFA);
    private static readonly Color Muted = Color.FromArgb(0x9A, 0xA9, 0xBD);

    public static string Duration(double minutes)
    {
        var seconds = (int)Math.Round(Math.Max(0, minutes) * 60, MidpointRounding.AwayFromZero);
        if (seconds < 60) return $"{seconds} сек";
        var totalMinutes = (seconds + 30) / 60;
        var hours = totalMinutes / 60;
        var rest = totalMinutes % 60;
        return hours == 0 ? $"{rest} мин" : rest == 0 ? $"{hours} ч" : $"{hours} ч {rest} мин";
    }

    public static string Hours(double minutes) => (minutes / 60).ToString("0.#", Ru);
    public static string Percent(double value) => $"{value.ToString("0", Ru)}%";
    public static Color PercentColor(double value) => value >= 85 ? Green : value >= 70 ? Yellow : Red;
    public static string Date(DateOnly date) => date.ToString("d MMMM", Ru);
    public static string LongDate(DateOnly date)
    {
        var text = date.ToString("dddd, d MMMM", Ru);
        return char.ToUpper(text[0], Ru) + text[1..];
    }
    public static string Time(TimeOnly time) => time.ToString("HH:mm", Ru);
    public static string Range(SlotDto slot) => $"{Time(slot.Start)}–{Time(slot.End)}";

    public static string DayShort(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "пн",
        DayOfWeek.Tuesday => "вт",
        DayOfWeek.Wednesday => "ср",
        DayOfWeek.Thursday => "чт",
        DayOfWeek.Friday => "пт",
        DayOfWeek.Saturday => "сб",
        DayOfWeek.Sunday => "вс",
        _ => ""
    };

    public static string DayLong(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "понедельник",
        DayOfWeek.Tuesday => "вторник",
        DayOfWeek.Wednesday => "среда",
        DayOfWeek.Thursday => "четверг",
        DayOfWeek.Friday => "пятница",
        DayOfWeek.Saturday => "суббота",
        DayOfWeek.Sunday => "воскресенье",
        _ => ""
    };

    public static string Parity(WeekParity parity) => parity switch
    {
        WeekParity.Every => "каждая",
        WeekParity.Odd => "нечётная",
        WeekParity.Even => "чётная",
        _ => ""
    };

    public static string Plural(int n, string one, string few, string many)
    {
        var value = Math.Abs((long)n);
        if ((value % 100) is >= 11 and <= 14) return many;
        return (value % 10) switch { 1 => one, >= 2 and <= 4 => few, _ => many };
    }

    public static string StatusText(AttendanceStatus? status) => status switch
    {
        AttendanceStatus.Present => "П",
        AttendanceStatus.Absent => "Н",
        AttendanceStatus.Excused => "Н (уваж.)",
        _ => "—"
    };

    public static string StatusSymbol(AttendanceStatus? status) => status switch
    {
        AttendanceStatus.Present => "П",
        AttendanceStatus.Absent => "Н",
        AttendanceStatus.Excused => "Н",
        _ => "·"
    };

    public static Color StatusColor(AttendanceStatus? status) => status switch
    {
        AttendanceStatus.Present => Green,
        AttendanceStatus.Absent => Red,
        AttendanceStatus.Excused => Yellow,
        _ => Muted
    };
    public static Color StatusBack(AttendanceStatus? status) => Color.FromArgb(40, StatusColor(status));

    public static string ExcuseReasonText(ExcuseReason reason) => reason switch
    {
        ExcuseReason.Illness => "Болезнь",
        ExcuseReason.Family => "Семейные обстоятельства",
        ExcuseReason.Official => "Учебная/служебная необходимость",
        _ => "Другая причина"
    };

    public static string AssessmentKindText(AssessmentKind kind) => kind switch
    {
        AssessmentKind.Test => "Тест",
        AssessmentKind.ControlWork => "Контрольная работа",
        AssessmentKind.Exam => "Экзамен",
        AssessmentKind.Credit => "Зачёт",
        _ => "Другое"
    };

    public static string AssessmentKindShort(AssessmentKind kind) => kind switch
    {
        AssessmentKind.Test => "Тест",
        AssessmentKind.ControlWork => "К/р",
        AssessmentKind.Exam => "Экз.",
        AssessmentKind.Credit => "Зачёт",
        _ => "—"
    };

    public static string MethodText(MarkMethod? method) => method switch
    {
        MarkMethod.Camera => "камера",
        MarkMethod.Manual => "вручную",
        MarkMethod.Demo => "отмечено",
        _ => "—"
    };

    public static DateOnly Monday(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
}
