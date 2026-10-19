using System.Drawing;
using BiStudent.Contracts;

namespace BiStudent.Client.Tests;

public sealed class UiFormatTests
{
    [Theory]
    [InlineData(-1, "0 сек")]
    [InlineData(0.5, "30 сек")]
    [InlineData(1, "1 мин")]
    [InlineData(60, "1 ч")]
    [InlineData(65, "1 ч 5 мин")]
    public void Duration(double value, string expected) => Assert.Equal(expected, UiFormat.Duration(value));

    [Theory]
    [InlineData(30, "0,5")]
    [InlineData(60, "1")]
    [InlineData(90, "1,5")]
    public void Hours(double value, string expected) => Assert.Equal(expected, UiFormat.Hours(value));

    [Theory]
    [InlineData(0, "0%")]
    [InlineData(84.6, "85%")]
    public void Percent(double value, string expected) => Assert.Equal(expected, UiFormat.Percent(value));

    [Theory]
    [InlineData(69, 0xF2, 0x78, 0x78)]
    [InlineData(70, 0xF5, 0xC4, 0x51)]
    [InlineData(85, 0x4A, 0xD8, 0x9A)]
    public void PercentColor(double value, int red, int green, int blue) =>
        Assert.Equal(Color.FromArgb(red, green, blue), UiFormat.PercentColor(value));

    [Fact]
    public void DatesTimesAndRange()
    {
        var date = new DateOnly(2026, 10, 16);
        Assert.Equal("16 октября", UiFormat.Date(date));
        Assert.Equal("Пятница, 16 октября", UiFormat.LongDate(date));
        Assert.Equal("09:05", UiFormat.Time(new TimeOnly(9, 5)));
        var slot = new SlotDto(1, 2, "Группа", "Предмет", null, DayOfWeek.Friday,
            new TimeOnly(9, 5), new TimeOnly(10, 30), WeekParity.Every, date, null, null);
        Assert.Equal("09:05–10:30", UiFormat.Range(slot));
        Assert.Equal(new DateOnly(2026, 10, 12), UiFormat.Monday(date));
    }

    [Theory]
    [InlineData(DayOfWeek.Monday, "пн", "понедельник")]
    [InlineData(DayOfWeek.Tuesday, "вт", "вторник")]
    [InlineData(DayOfWeek.Wednesday, "ср", "среда")]
    [InlineData(DayOfWeek.Thursday, "чт", "четверг")]
    [InlineData(DayOfWeek.Friday, "пт", "пятница")]
    [InlineData(DayOfWeek.Saturday, "сб", "суббота")]
    [InlineData(DayOfWeek.Sunday, "вс", "воскресенье")]
    public void DayNames(DayOfWeek day, string shortName, string longName)
    {
        Assert.Equal(shortName, UiFormat.DayShort(day));
        Assert.Equal(longName, UiFormat.DayLong(day));
    }

    [Theory]
    [InlineData(WeekParity.Every, "каждая")]
    [InlineData(WeekParity.Odd, "нечётная")]
    [InlineData(WeekParity.Even, "чётная")]
    public void Parity(WeekParity value, string expected) => Assert.Equal(expected, UiFormat.Parity(value));

    [Theory]
    [InlineData(1, "студент")]
    [InlineData(2, "студента")]
    [InlineData(5, "студентов")]
    [InlineData(11, "студентов")]
    [InlineData(21, "студент")]
    public void Plural(int count, string expected) =>
        Assert.Equal(expected, UiFormat.Plural(count, "студент", "студента", "студентов"));

    [Theory]
    [InlineData(null, "—", "·", 0x9A, 0xA9, 0xBD)]
    [InlineData(AttendanceStatus.Present, "П", "П", 0x4A, 0xD8, 0x9A)]
    [InlineData(AttendanceStatus.Absent, "Н", "Н", 0xF2, 0x78, 0x78)]
    [InlineData(AttendanceStatus.Excused, "Н (уваж.)", "Н", 0xF5, 0xC4, 0x51)]
    public void Status(AttendanceStatus? status, string text, string symbol, int red, int green, int blue)
    {
        Assert.Equal(text, UiFormat.StatusText(status));
        Assert.Equal(symbol, UiFormat.StatusSymbol(status));
        var color = Color.FromArgb(red, green, blue);
        Assert.Equal(color, UiFormat.StatusColor(status));
        Assert.Equal(Color.FromArgb(40, color), UiFormat.StatusBack(status));
    }

    [Theory]
    [InlineData(ExcuseReason.Illness, "Болезнь")]
    [InlineData(ExcuseReason.Family, "Семейные обстоятельства")]
    [InlineData(ExcuseReason.Official, "Учебная/служебная необходимость")]
    [InlineData(ExcuseReason.Other, "Другая причина")]
    public void ExcuseReasonText(ExcuseReason reason, string expected) =>
        Assert.Equal(expected, UiFormat.ExcuseReasonText(reason));

    [Theory]
    [InlineData(AssessmentKind.Test, "Тест", "Тест")]
    [InlineData(AssessmentKind.ControlWork, "Контрольная работа", "К/р")]
    [InlineData(AssessmentKind.Exam, "Экзамен", "Экз.")]
    [InlineData(AssessmentKind.Credit, "Зачёт", "Зачёт")]
    [InlineData(AssessmentKind.Other, "Другое", "—")]
    public void AssessmentKindNames(AssessmentKind kind, string text, string shortText)
    {
        Assert.Equal(text, UiFormat.AssessmentKindText(kind));
        Assert.Equal(shortText, UiFormat.AssessmentKindShort(kind));
    }

    [Theory]
    [InlineData(null, "—")]
    [InlineData(MarkMethod.Camera, "камера")]
    [InlineData(MarkMethod.Manual, "вручную")]
    [InlineData(MarkMethod.Demo, "отмечено")]
    public void MethodText(MarkMethod? method, string expected) =>
        Assert.Equal(expected, UiFormat.MethodText(method));
}
