using System.Text;
using BiStudent.Contracts;
using BiStudent.Server.Services;
using ClosedXML.Excel;
using Xunit;

namespace BiStudent.Tests;

public sealed class StudentAndExportTests
{
    [Fact]
    public async Task ImportCsvAsync_AddsStudentsSkipsDuplicatesAndCreatesGroups()
    {
        using var test = new TestDb();
        test.AddGroup("ИВТ-1", "Иван Иванов");
        using var csv = new MemoryStream(Encoding.UTF8.GetBytes("ФИО;Группа\nИван Иванов;ИВТ-1\nАнна Петрова;ИВТ-1\nПётр Сидоров;ИВТ-2\nПётр Сидоров;ИВТ-2\n"));
        var result = await test.Students.ImportCsvAsync(csv);
        Assert.Equal(2, result.Added);
        Assert.Equal(2, result.Skipped);
        Assert.Equal(2, test.Db.Groups.Count());
        Assert.Equal(3, test.Db.Students.Count());
        Assert.Contains(test.Db.Groups, x => x.Name == "ИВТ-2");
    }

    [Fact]
    public async Task AddGroupAsync_RejectsDuplicateName()
    {
        using var test = new TestDb();
        await test.Students.AddGroupAsync("ИВТ-1");
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Students.AddGroupAsync(" ИВТ-1 "));
    }

    [Fact]
    public void JournalXlsx_OpensAndContainsStudentAndPercent()
    {
        using var test = new TestDb();
        var bytes = test.Export.JournalXlsx(Matrix(), "Журнал");
        using var stream = new MemoryStream(bytes);
        using var book = new XLWorkbook(stream);
        var sheet = book.Worksheet("Журнал");
        Assert.Equal("Иван Иванов", sheet.Cell(5, 1).GetString());
        Assert.Equal(50d, sheet.Cell(5, 4).GetDouble());
    }

    [Fact]
    public void JournalCsv_StartsWithBomAndUsesSemicolons()
    {
        using var test = new TestDb();
        var csv = test.Export.JournalCsv(Matrix());
        Assert.StartsWith("\uFEFF", csv);
        Assert.Contains("\"Студент\";\"01.09.2026 09:00 Алгебра\";", csv);
        Assert.Contains("\"Иван Иванов\";\"+\";\"Н\";\"50.0\"", csv);
    }

    private static JournalMatrix Matrix() => new(
        "Ирина Иванова", "ИВТ-1", new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 8),
        [new JournalColumn(1, new DateOnly(2026, 9, 1), new TimeOnly(9, 0), "Алгебра"),
            new JournalColumn(2, new DateOnly(2026, 9, 8), new TimeOnly(9, 0), "Физика")],
        [new JournalStudent(1, "Иван Иванов", [AttendanceStatus.Present, AttendanceStatus.Absent], 50d)]);
}
