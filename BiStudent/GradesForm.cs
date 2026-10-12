using System.Globalization;

namespace BiStudent;

internal sealed class GradesForm : Form
{
    private readonly AttendanceStore store;
    private readonly Student student;
    private readonly TeacherSession teacher;
    private readonly TextBox subject = FormStyle.Input("Например, Математика");
    private readonly TextBox date = FormStyle.Input("ГГГГ-ММ-ДД");
    private readonly TextBox score = FormStyle.Input("0–100");
    private readonly TextBox comment = FormStyle.Input("Тема работы или комментарий");
    private readonly DataGridView table = MainForm.Grid();
    private readonly Label summary = FormStyle.Caption("");
    private readonly Label feedback = FormStyle.Caption("");
    private readonly ModernButton save = new() { Text = "Добавить оценку", Primary = true, Dock = DockStyle.Fill };
    private int? editingId;
    private List<GradeRow> rows = [];

    public GradesForm(AttendanceStore store, Student student, TeacherSession teacher)
    {
        this.store = store; this.student = student; this.teacher = teacher;
        FormStyle.Dialog(this, "BiStudent · Оценки", new Size(1060, 740));
        FormBorderStyle = FormBorderStyle.Sizable; MaximizeBox = true; DoubleBuffered = true;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(24), BackColor = Theme.Background };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        var title = FormStyle.Caption(student.Name + " · " + student.GroupName); title.Font = Theme.Font(20, FontStyle.Bold); title.ForeColor = Theme.Text; title.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(title, 0, 0);
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Background };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var entry = new SmoothPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 16, 0), Padding = new Padding(16) };
        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 12, BackColor = Theme.Surface };
        foreach (var height in new[] { 22, 36, 22, 36, 22, 36, 22, 65, 46, 46, 50 }) fields.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        fields.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        fields.Controls.Add(FormStyle.Caption("ПРЕДМЕТ"), 0, 0); fields.Controls.Add(subject, 0, 1);
        fields.Controls.Add(FormStyle.Caption("ДАТА · ГГГГ-ММ-ДД"), 0, 2); fields.Controls.Add(date, 0, 3);
        fields.Controls.Add(FormStyle.Caption("ОЦЕНКА · ОТ 0 ДО 100"), 0, 4); fields.Controls.Add(score, 0, 5);
        fields.Controls.Add(FormStyle.Caption("КОММЕНТАРИЙ"), 0, 6); comment.Multiline = true; comment.MaxLength = 500; fields.Controls.Add(comment, 0, 7);
        subject.MaxLength = 120; subject.AutoCompleteMode = AutoCompleteMode.SuggestAppend; subject.AutoCompleteSource = AutoCompleteSource.CustomSource;
        subject.AutoCompleteCustomSource.AddRange(store.Lessons().Select(x => x.Subject).Distinct().ToArray());
        fields.Controls.Add(save, 0, 8);
        var reset = new ModernButton { Text = "Новая оценка", Dock = DockStyle.Fill }; reset.Click += (_, _) => ResetEntry(); fields.Controls.Add(reset, 0, 9);
        feedback.ForeColor = Theme.Red; feedback.TextAlign = ContentAlignment.MiddleLeft; fields.Controls.Add(feedback, 0, 10);
        entry.Controls.Add(fields); body.Controls.Add(entry, 0, 0);
        var history = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Theme.Background };
        history.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); history.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); history.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        summary.TextAlign = ContentAlignment.MiddleLeft; summary.Font = Theme.Font(11, FontStyle.Bold); summary.ForeColor = Theme.Text; history.Controls.Add(summary, 0, 0);
        var card = new SmoothPanel { Dock = DockStyle.Fill, Padding = new Padding(4) }; card.Controls.Add(table); history.Controls.Add(card, 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        var edit = new ModernButton { Text = "Изменить", Width = 140 }; edit.Click += (_, _) => EditSelected();
        var delete = new ModernButton { Text = "Удалить оценку", Width = 160 }; delete.Click += (_, _) => DeleteSelected();
        actions.Controls.Add(edit); actions.Controls.Add(delete); history.Controls.Add(actions, 0, 2);
        body.Controls.Add(history, 1, 0); layout.Controls.Add(body, 0, 1);
        var hint = FormStyle.Caption("Преподаватель: " + teacher.Name + " · Оценки сохраняются на этом компьютере."); hint.Font = Theme.Font(9); layout.Controls.Add(hint, 0, 2);
        Controls.Add(layout); save.Click += (_, _) => Save(); table.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) EditSelected(); };
        ResetEntry(); RefreshRows();
    }

    private void ResetEntry()
    {
        editingId = null; save.Text = "Добавить оценку"; date.Text = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        score.Clear(); comment.Clear(); feedback.Text = "";
    }

    private void RefreshRows()
    {
        rows = store.Grades(student.Id);
        table.DataSource = rows.Select(x => new { x.Id, Дата = x.Date.ToString("dd.MM.yyyy"), Предмет = x.Subject, Оценка = x.Score, Комментарий = x.Comment }).ToList();
        if (table.Columns["Id"] is { } id) id.Visible = false;
        if (table.Columns["Оценка"] is { } grade) grade.FillWeight = 55;
        summary.Text = rows.Count == 0 ? "Оценок пока нет" : $"Оценок: {rows.Count} · Средний балл: {rows.Average(x => x.Score):0.0} / 100";
    }

    private GradeRow? Selected() => table.CurrentRow?.Cells["Id"].Value is int id ? rows.FirstOrDefault(x => x.Id == id) : null;

    private void EditSelected()
    {
        if (Selected() is not { } row) return;
        editingId = row.Id; subject.Text = row.Subject; date.Text = row.Date.ToString("yyyy-MM-dd"); score.Text = row.Score.ToString(CultureInfo.InvariantCulture); comment.Text = row.Comment;
        save.Text = "Сохранить изменения"; feedback.Text = "";
    }

    private void Save()
    {
        if (!DateOnly.TryParseExact(date.Text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) { feedback.Text = "Дата должна быть в формате ГГГГ-ММ-ДД."; return; }
        if (!int.TryParse(score.Text.Trim(), out var value) || value is < 0 or > 100) { feedback.Text = "Введите целое число от 0 до 100."; return; }
        try { store.SaveGrade(editingId, student.Id, subject.Text, day, value, comment.Text, teacher); ResetEntry(); RefreshRows(); }
        catch (Exception ex) { feedback.Text = ex is ArgumentException or InvalidOperationException ? ex.Message : "Не удалось сохранить оценку."; }
    }

    private void DeleteSelected()
    {
        if (Selected() is not { } row) return;
        if (MessageBox.Show(this, $"Удалить оценку {row.Score} по предмету «{row.Subject}»?", "Оценки", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try { store.DeleteGrade(row.Id, student.Id); ResetEntry(); RefreshRows(); }
        catch { feedback.Text = "Не удалось удалить оценку."; }
    }
}
