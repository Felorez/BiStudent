using System.Globalization;

namespace BiStudent;

internal sealed class CloudGradesForm : Form
{
    private readonly CloudWorkspace workspace;
    private readonly string studentId;
    private readonly List<CloudSubject> subjects;
    private List<CloudGrade> grades = [];
    private readonly DataGridView table = MainForm.Grid();
    private readonly ComboBox subject = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Theme.SurfaceHover, ForeColor = Theme.Text, Font = Theme.Font(10), DropDownWidth = 640 };
    private readonly TextBox score = FormStyle.Input("0–100");
    private readonly TextBox date = FormStyle.Input("ГГГГ-ММ-ДД");
    private readonly TextBox comment = FormStyle.Input("Комментарий");
    private readonly Label feedback = FormStyle.Caption("");
    private readonly ModernButton save = new() { Text = "Добавить новую оценку", Primary = true, Dock = DockStyle.Fill };
    private readonly ModernButton edit = new() { Text = "Изменить выбранную", Dock = DockStyle.Fill };
    private readonly ModernButton clear = new() { Text = "Удалить выбранную", Dock = DockStyle.Fill };
    private readonly ModernButton refresh = new() { Text = "Сохранить изменения", Dock = DockStyle.Fill };
    private bool busy;
    private string? selectedGradeId;

    public CloudGradesForm(CloudWorkspace workspace, string studentId, string studentName, List<CloudSubject> subjects)
    {
        this.workspace = workspace; this.studentId = studentId; this.subjects = subjects;
        FormStyle.Dialog(this, "BiStudent · Оценки в Firebase", new Size(1260, 740)); FormBorderStyle = FormBorderStyle.Sizable; MaximizeBox = true; DoubleBuffered = true;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(24) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        var title = FormStyle.Caption(studentName); title.Font = Theme.Font(20, FontStyle.Bold); title.ForeColor = Theme.Text; title.TextAlign = ContentAlignment.MiddleLeft; layout.Controls.Add(title, 0, 0);
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 365)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var entry = new SmoothPanel { Dock = DockStyle.Fill, Padding = new Padding(16), Margin = new Padding(0, 0, 16, 0) };
        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 13, BackColor = Theme.Surface };
        foreach (var height in new[] { 26, 36, 26, 36, 26, 36, 26, 45, 42, 42, 42, 42 }) fields.RowStyles.Add(new RowStyle(SizeType.Absolute, height)); fields.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        subject.DrawMode = DrawMode.OwnerDrawFixed; subject.ItemHeight = 24;
        subject.DrawItem += (_, e) =>
        {
            using var brush = new SolidBrush((e.State & DrawItemState.Selected) != 0 ? Theme.SurfaceHover : Theme.Surface);
            e.Graphics.FillRectangle(brush, e.Bounds);
            if (e.Index >= 0) TextRenderer.DrawText(e.Graphics, subjects[e.Index].Name, subject.Font, e.Bounds, Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };
        subject.DisplayMember = nameof(CloudSubject.Name);
        subject.Items.AddRange(subjects.Cast<object>().ToArray()); subject.SelectedIndex = 0;
        fields.Controls.Add(FormStyle.Caption("ПРЕДМЕТ"), 0, 0); fields.Controls.Add(subject, 0, 1);
        fields.Controls.Add(FormStyle.Caption("ДАТА · ГГГГ-ММ-ДД"), 0, 2); fields.Controls.Add(date, 0, 3);
        fields.Controls.Add(FormStyle.Caption("ОЦЕНКА · ОТ 0 ДО 100"), 0, 4); fields.Controls.Add(score, 0, 5);
        fields.Controls.Add(FormStyle.Caption("КОММЕНТАРИЙ"), 0, 6); comment.Multiline = true; comment.MaxLength = 500; fields.Controls.Add(comment, 0, 7);
        fields.Controls.Add(save, 0, 8); fields.Controls.Add(edit, 0, 9); fields.Controls.Add(clear, 0, 10); fields.Controls.Add(refresh, 0, 11);
        entry.Controls.Add(fields); body.Controls.Add(entry, 0, 0);
        var card = new SmoothPanel { Dock = DockStyle.Fill, Padding = new Padding(4) }; card.Controls.Add(table); body.Controls.Add(card, 1, 0); layout.Controls.Add(body, 0, 1);
        feedback.Font = Theme.Font(9); feedback.TextAlign = ContentAlignment.MiddleLeft; layout.Controls.Add(feedback, 0, 2); Controls.Add(layout);
        subject.SelectedIndexChanged += (_, _) => { selectedGradeId = null; FillEntry(); DrawRows(); ShowSummary(); };
        table.CellClick += (_, e) => { if (e.RowIndex >= 0 && table.Rows[e.RowIndex].Cells["Id"].Value is string id) { selectedGradeId = id; FillEntry(); } };
        save.Click += async (_, _) => await SaveAsync(false, true); edit.Click += async (_, _) => await SaveAsync(false, false); clear.Click += async (_, _) => await SaveAsync(true, false); refresh.Click += async (_, _) => await workspace.SaveAsync();
        Shown += (_, _) => RefreshFromCache(); workspace.Changed += RefreshFromCache;
        FillEntry(); SetBusy(false);
    }

    private void SetBusy(bool value) { busy = value; save.Enabled = edit.Enabled = clear.Enabled = refresh.Enabled = subject.Enabled = score.Enabled = date.Enabled = comment.Enabled = table.Enabled = !value; }
    private CloudGrade? Selected() => grades.FirstOrDefault(x => x.Id == selectedGradeId && x.Score is not null);
    private List<CloudGrade> SubjectGrades() => grades.Where(x => x.SubjectId == (subject.SelectedItem as CloudSubject)?.Id && x.Score is not null).OrderByDescending(x => x.Date).ThenByDescending(x => x.UpdateTime).ToList();
    private void ShowSummary()
    {
        var rows = SubjectGrades(); var average = CloudFirestore.Average(rows);
        feedback.ForeColor = Theme.Muted; feedback.Text = $"Оценок по предмету: {rows.Count}/100 · Средний балл: {average?.ToString("0.00") ?? "—"} · Добавление сохраняет новую запись. Для изменения выберите строку.";
    }
    private void FillEntry()
    {
        var grade = Selected(); score.Text = grade?.Score?.ToString(CultureInfo.InvariantCulture) ?? "";
        date.Text = (grade?.Date ?? DateOnly.FromDateTime(DateTime.Today)).ToString("yyyy-MM-dd"); comment.Text = grade?.Comment ?? "";
    }
    private void DrawRows()
    {
        table.DataSource = SubjectGrades().Select(g => new { g.Id, Предмет = subjects.First(s => s.Id == g.SubjectId).Name, Оценка = g.Score!.Value, Дата = g.Date?.ToString("dd.MM.yyyy") ?? "—", Комментарий = g.Comment }).ToList();
        table.Columns["Id"]!.Visible = false; table.Columns["Предмет"]!.FillWeight = 260; table.Columns["Оценка"]!.FillWeight = 55; table.Columns["Дата"]!.FillWeight = 75; table.Columns["Комментарий"]!.FillWeight = 110;
        table.DefaultCellStyle.WrapMode = DataGridViewTriState.True; table.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        foreach (DataGridViewRow row in table.Rows) row.Height = Math.Max(row.Height, 48);
        table.ClearSelection();
    }
    private void RefreshFromCache()
    {
        if (IsDisposed) return;
        grades = workspace.Snapshot?.Grades.Where(x => x.StudentId == studentId).ToList() ?? [];
        DrawRows(); ShowSummary(); SetBusy(workspace.IsSaving);
        refresh.Enabled = workspace.HasChanges && !workspace.IsSaving;
        if (workspace.Error is not null) { feedback.ForeColor = Theme.Red; feedback.Text = workspace.Error; }
        else if (workspace.IsSaving) feedback.Text = "Сохранение изменений в Firebase…";
        else if (workspace.HasChanges) feedback.Text += " · Есть несохранённые изменения";
    }
    private async Task SaveAsync(bool clearing, bool adding)
    {
        if (busy) return;
        var grade = Selected();
        if (!adding && grade is null) { feedback.Text = "Сначала выберите оценку в журнале."; return; }
        if (subject.SelectedItem is not CloudSubject item) return;
        int? value = null; DateOnly? day = null;
        if (!clearing)
        {
            if (!int.TryParse(score.Text.Trim(), out var number) || number is < 0 or > 100) { feedback.ForeColor = Theme.Red; feedback.Text = "Введите целую оценку от 0 до 100."; return; }
            if (!DateOnly.TryParseExact(date.Text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) { feedback.ForeColor = Theme.Red; feedback.Text = "Введите дату в формате ГГГГ-ММ-ДД."; return; }
            value = number; day = parsed;
        }
        await Task.CompletedTask;
        try
        {
            if (adding) workspace.Add(studentId, item.Id, value!.Value, day!.Value, comment.Text);
            else workspace.Edit(grade!.Id, value, day, clearing ? "" : comment.Text);
            selectedGradeId = null; FillEntry(); RefreshFromCache();
        }
        catch (Exception ex) { if (!IsDisposed) ShowError(ex); }
        finally { if (!IsDisposed) refresh.Enabled = workspace.HasChanges && !workspace.IsSaving; }
    }
    protected override void OnFormClosed(FormClosedEventArgs e) { workspace.Changed -= RefreshFromCache; base.OnFormClosed(e); }
    private void ShowError(Exception ex) { feedback.ForeColor = Theme.Red; feedback.Text = ex is InvalidOperationException or ArgumentException ? ex.Message : "Нет соединения с Firebase. Проверьте интернет и обновите список."; }
    protected override void OnFormClosing(FormClosingEventArgs e) { if (busy && Visible) { e.Cancel = true; feedback.Text = "Дождитесь завершения запроса Firebase."; } base.OnFormClosing(e); }
}
