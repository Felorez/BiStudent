using BiStudent.Contracts;
using System.Globalization;
using System.Text;
using BiStudent.Server.Data;
using ClosedXML.Excel;

namespace BiStudent.Server.Services;

public sealed class ExportService(AppDbContext db)
{
    private static string Symbol(AttendanceStatus? status) => status switch
    {
        AttendanceStatus.Present => "+", AttendanceStatus.Late => "О",
        AttendanceStatus.Absent => "Н", AttendanceStatus.Excused => "У", _ => ""
    };

    private void CountReport()
    {
        foreach (var key in new[] { "stats.reports_exported", $"stats.reports_exported.{DateOnly.FromDateTime(DateTime.Now):yyyy-MM-dd}" })
        {
            var setting = db.AppSettings.Find(key);
            if (setting is null) { setting = new AppSetting { Key = key, Value = "0" }; db.AppSettings.Add(setting); }
            setting.Value = (int.Parse(setting.Value, CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture);
        }
        db.SaveChanges();
    }

    public byte[] JournalXlsx(JournalMatrix matrix, string title)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Журнал");
        sheet.Cell(1, 1).Value = title;
        sheet.Cell(2, 1).Value = $"Группа: {matrix.GroupName}";
        sheet.Cell(2, 2).Value = $"Преподаватель: {matrix.TeacherName}";
        sheet.Cell(3, 1).Value = $"Период: {matrix.From:dd.MM.yyyy}–{matrix.To:dd.MM.yyyy}";
        sheet.Cell(4, 1).Value = "Студент";
        for (var i = 0; i < matrix.Columns.Count; i++)
        {
            var column = matrix.Columns[i];
            sheet.Cell(4, i + 2).Value = $"{column.Date:dd.MM} {column.Start:HH:mm}\n{column.Subject}";
        }
        var totalColumn = matrix.Columns.Count + 2;
        sheet.Cell(4, totalColumn).Value = "Присутствие, %";
        for (var row = 0; row < matrix.Students.Count; row++)
        {
            var student = matrix.Students[row];
            sheet.Cell(row + 5, 1).Value = student.FullName;
            for (var i = 0; i < student.Statuses.Count; i++)
                sheet.Cell(row + 5, i + 2).Value = Symbol(student.Statuses[i]);
            sheet.Cell(row + 5, totalColumn).Value = student.Percent;
            sheet.Cell(row + 5, totalColumn).Style.NumberFormat.Format = "0.0";
        }
        var lastRow = Math.Max(5, matrix.Students.Count + 4);
        sheet.Range(4, 1, 4, totalColumn).Style.Font.Bold = true;
        sheet.Range(4, 1, lastRow, totalColumn).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        if (matrix.Columns.Count > 0 && matrix.Students.Count > 0)
        {
            var cells = sheet.Range(5, 2, lastRow, totalColumn - 1);
            cells.AddConditionalFormat().WhenEquals("\"+\"").Fill.SetBackgroundColor(XLColor.LightGreen);
            cells.AddConditionalFormat().WhenEquals("\"О\"").Fill.SetBackgroundColor(XLColor.LightYellow);
            cells.AddConditionalFormat().WhenEquals("\"Н\"").Fill.SetBackgroundColor(XLColor.LightPink);
            cells.AddConditionalFormat().WhenEquals("\"У\"").Fill.SetBackgroundColor(XLColor.LightBlue);
        }
        sheet.SheetView.FreezeRows(4);
        sheet.Columns().AdjustToContents();
        using var output = new MemoryStream();
        workbook.SaveAs(output);
        CountReport();
        return output.ToArray();
    }

    public string JournalCsv(JournalMatrix matrix)
    {
        static string Escape(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
        var lines = new List<string> {
            string.Join(';', new[] { "Студент" }.Concat(matrix.Columns.Select(x => $"{x.Date:dd.MM.yyyy} {x.Start:HH:mm} {x.Subject}")).Append("Присутствие, %").Select(Escape))
        };
        lines.AddRange(matrix.Students.Select(student => string.Join(';',
            new[] { student.FullName }.Concat(student.Statuses.Select(Symbol))
                .Append(student.Percent.ToString("0.0", CultureInfo.InvariantCulture)).Select(Escape))));
        return "\uFEFF" + string.Join("\r\n", lines) + "\r\n";
    }
}
