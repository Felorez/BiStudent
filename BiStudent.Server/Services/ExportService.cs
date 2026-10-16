using BiStudent.Contracts;
using System.Globalization;
using System.Text;
using BiStudent.Server.Data;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;

namespace BiStudent.Server.Services;

public sealed class ExportService(AppDbContext db)
{
    public static string ExcuseReasonText(ExcuseReason reason) => reason switch
    {
        ExcuseReason.Illness => "Болезнь",
        ExcuseReason.Family => "Семейные обстоятельства",
        ExcuseReason.Official => "Учебная/служебная необходимость",
        _ => "Другая причина"
    };

    private static string Symbol(JournalCell cell, bool csv = false)
    {
        var status = cell.Status switch
        {
            AttendanceStatus.Present => "П",
            AttendanceStatus.Absent => "Н",
            AttendanceStatus.Excused => csv ? "Н(у)" : "Н",
            _ => ""
        };
        return cell.Score is int score ? $"{status} {score}".Trim() : status;
    }

    private void CountReport()
    {
        foreach (var key in new[] { "stats.reports_exported", $"stats.reports_exported.{DateOnly.FromDateTime(DateTime.Now):yyyy-MM-dd}" })
        {
            db.Database.ExecuteSql(
                $"INSERT INTO \"AppSettings\" (\"Key\",\"Value\") VALUES ({key}, '1') ON CONFLICT(\"Key\") DO UPDATE SET \"Value\" = CAST(CAST(\"Value\" AS INTEGER) + 1 AS TEXT)");
        }
    }

    public byte[] JournalXlsx(JournalMatrix matrix, string title)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Журнал");
        var lastColumn = matrix.Columns.Count + 7;
        sheet.Cell(1, 1).Value = title;
        sheet.Range(1, 1, 1, lastColumn).Merge();
        sheet.Cell(1, 1).Style.Font.Bold = true;
        sheet.Cell(1, 1).Style.Font.FontSize = 14;
        sheet.Cell(2, 1).Value = $"Группа: {matrix.GroupName}";
        sheet.Cell(3, 1).Value = $"Дисциплина: {matrix.Subject ?? "все дисциплины"}";
        sheet.Cell(4, 1).Value = $"Преподаватель: {matrix.TeacherName}";
        sheet.Cell(5, 1).Value = $"Период: {matrix.From:dd.MM.yyyy} — {matrix.To:dd.MM.yyyy}";
        sheet.Cell(7, 1).Value = "№";
        sheet.Cell(7, 2).Value = "ФИО студента";
        for (var i = 0; i < matrix.Columns.Count; i++)
        {
            var column = matrix.Columns[i];
            sheet.Cell(7, i + 3).Value = column.Kind == JournalColumnKind.Lesson
                ? $"{column.Date:dd.MM}\n{column.Start:HH:mm}"
                : $"{column.Date:dd.MM}\n{AssessmentKindText(column.AssessmentKind)}\n{column.Title}";
        }
        var summary = matrix.Columns.Count + 3;
        sheet.Cell(7, summary).Value = "Пропущено, ч";
        sheet.Cell(7, summary + 1).Value = "в т.ч. по уваж., ч";
        sheet.Cell(7, summary + 2).Value = "Посещаемость, %";
        sheet.Cell(7, summary + 3).Value = "Средний балл";
        sheet.Cell(7, summary + 4).Value = "Итог";
        for (var row = 0; row < matrix.Students.Count; row++)
        {
            var student = matrix.Students[row];
            var rowNumber = row + 8;
            sheet.Cell(rowNumber, 1).Value = row + 1;
            sheet.Cell(rowNumber, 2).Value = student.FullName;
            for (var i = 0; i < student.Cells.Count; i++)
            {
                var column = matrix.Columns[i];
                var value = student.Cells[i];
                var cell = sheet.Cell(rowNumber, i + 3);
                if (column.Kind == JournalColumnKind.Assessment)
                {
                    if (value.Score is int score) cell.Value = score;
                    continue;
                }
                cell.Value = Symbol(value);
                if (value.Status == AttendanceStatus.Present) cell.Style.Fill.BackgroundColor = XLColor.LightGreen;
                if (value.Status == AttendanceStatus.Absent) cell.Style.Fill.BackgroundColor = XLColor.LightPink;
                if (value.Status == AttendanceStatus.Excused)
                {
                    cell.Style.Fill.BackgroundColor = XLColor.LightYellow;
                    var note = $"Причина: {ExcuseReasonText(value.Reason ?? ExcuseReason.Other)}.";
                    if (value.HasDocument) note += " Документ предоставлен.";
                    if (value.Comment is not null) note += $" {value.Comment}";
                    cell.GetComment().AddText(note);
                }
            }
            sheet.Cell(rowNumber, summary).Value = student.MissedHours;
            sheet.Cell(rowNumber, summary + 1).Value = student.ExcusedHours;
            sheet.Cell(rowNumber, summary + 2).Value = student.Percent;
            sheet.Cell(rowNumber, summary + 2).Style.NumberFormat.Format = "0.0";
            if (student.Average is double average) sheet.Cell(rowNumber, summary + 3).Value = average;
            sheet.Cell(rowNumber, summary + 3).Style.NumberFormat.Format = "0.0";
            if (student.Final is int final) sheet.Cell(rowNumber, summary + 4).Value = final;
        }
        var lastRow = Math.Max(7, matrix.Students.Count + 7);
        var table = sheet.Range(7, 1, lastRow, lastColumn);
        table.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        table.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        table.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        var header = sheet.Range(7, 1, 7, lastColumn);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.LightGray;
        header.Style.Alignment.WrapText = true;
        if (matrix.Students.Count > 0)
            sheet.Range(8, 2, lastRow, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
        sheet.Cell(lastRow + 2, 1).Value =
            "Обозначения: П — присутствовал(а), Н — отсутствовал(а); жёлтым отмечено отсутствие по уважительной причине (причина — в примечании к ячейке). Баллы — по 100-балльной шкале.";
        sheet.Cell(lastRow + 3, 1).Value = $"Дата формирования: {DateTime.Now:dd.MM.yyyy}";
        sheet.Cell(lastRow + 4, 1).Value = $"Преподаватель: ____________ / {matrix.TeacherName} /";
        sheet.SheetView.Freeze(7, 2);
        sheet.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        sheet.PageSetup.FitToPages(1, 0);
        sheet.PageSetup.SetRowsToRepeatAtTop(7, 7);
        sheet.Column(1).Width = 5;
        sheet.Column(2).Width = 32;
        sheet.Columns(3, lastColumn).AdjustToContents();
        using var output = new MemoryStream();
        workbook.SaveAs(output);
        CountReport();
        return output.ToArray();
    }

    public string JournalCsv(JournalMatrix matrix)
    {
        static string Escape(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
        static string EscapeText(string value) => Escape(value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r'
            ? "'" + value : value);
        var lines = new List<string> {
            string.Join(';', new[] { "№", "ФИО студента" }
                .Concat(matrix.Columns.Select(x => x.Kind == JournalColumnKind.Lesson
                    ? $"{x.Date:dd.MM.yyyy} {x.Start:HH:mm} {x.Subject}"
                    : $"{x.Date:dd.MM.yyyy} {AssessmentKindText(x.AssessmentKind)} {x.Title}"))
                .Concat(["Пропущено, ч", "в т.ч. по уваж., ч", "Посещаемость, %",
                    "Средний балл", "Итог"]).Select(EscapeText))
        };
        lines.AddRange(matrix.Students.Select((student, index) => string.Join(';',
            new[] { Escape((index + 1).ToString(CultureInfo.InvariantCulture)), EscapeText(student.FullName) }
                .Concat(student.Cells.Select(x => Escape(Symbol(x, true))))
                .Concat([
                    Escape(student.MissedHours.ToString(CultureInfo.InvariantCulture)),
                    Escape(student.ExcusedHours.ToString(CultureInfo.InvariantCulture)),
                    Escape(student.Percent.ToString("0.0", CultureInfo.InvariantCulture)),
                    Escape(student.Average?.ToString("0.0", CultureInfo.InvariantCulture) ?? ""),
                    Escape(student.Final?.ToString(CultureInfo.InvariantCulture) ?? "")
                ]))));
        CountReport();
        return "\uFEFF" + string.Join("\r\n", lines) + "\r\n";
    }

    private static string AssessmentKindText(AssessmentKind? kind) => kind switch
    {
        AssessmentKind.Test => "Тест",
        AssessmentKind.ControlWork => "К/р",
        AssessmentKind.Exam => "Экзамен",
        AssessmentKind.Credit => "Зачёт",
        _ => "—"
    };
}
