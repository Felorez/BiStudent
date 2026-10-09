using System.Globalization;
using System.Text;

namespace BiStudent;

internal static class JournalExport
{
    public static void Write(string path, IEnumerable<JournalRow> rows)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.NewLine = "\r\n";
        writer.WriteLine("Дата;День;Предмет;Группа;Время занятия;Студент;Статус;Отмечен;Способ");
        foreach (var row in rows)
        {
            var day = ShortDay(row.Date.DayOfWeek);
            var time = $"{row.Start:hh\\:mm}–{row.End:hh\\:mm}";
            writer.WriteLine(string.Join(";", row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), day, Escape(row.Subject), Escape(row.GroupName), time, Escape(row.StudentName), row.SeenAt is null ? "Нет отметки" : "Присутствует", row.SeenAt?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "", Escape(row.Method)));
        }
    }

    internal static string Escape(string value)
    {
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value;
        return value.IndexOfAny([';', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }

    internal static string ShortDay(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "Пн",
        DayOfWeek.Tuesday => "Вт",
        DayOfWeek.Wednesday => "Ср",
        DayOfWeek.Thursday => "Чт",
        DayOfWeek.Friday => "Пт",
        DayOfWeek.Saturday => "Сб",
        _ => "Вс"
    };
}
