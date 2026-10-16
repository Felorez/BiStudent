using BiStudent.Contracts;

namespace BiStudent;

internal sealed class GradeEditor : IDisposable
{
    private readonly DialogShell shell;
    private readonly ApiClient api;
    private readonly JournalColumnDto? column;
    private readonly int groupId;
    private readonly string? subject;
    private readonly int studentId;
    private readonly string studentName;
    private readonly TextBox score = Ui.Input("Без балла");
    private readonly Label error = Ui.Label("", 9, Theme.Red);
    private readonly Label reasonLabel = Ui.Label("", 9, Theme.Muted);
    private readonly ModernButton save = new() { Text = "Сохранить", Primary = true };
    private readonly Dictionary<AttendanceStatus, ModernButton> statusButtons = [];
    private ModernButton cancel = null!;
    private AttendanceStatus? status;
    private ExcuseReason? reason;
    private string? comment;
    private bool hasDocument;
    private bool saving;

    public GradeEditor(ApiClient api, JournalColumnDto column, JournalCellDto cell,
        int studentId, string studentName, bool scoreOnly = false, AttendanceRowDto? attendance = null, bool attendanceOnly = false)
    {
        this.api = api;
        this.column = column;
        this.studentId = studentId;
        this.studentName = studentName;
        status = cell.Status;
        reason = attendance?.Reason ?? cell.Reason;
        comment = attendance?.Comment ?? cell.Comment;
        hasDocument = attendance?.HasDocument ?? false;
        shell = new DialogShell(attendanceOnly ? "Посещаемость" : "Отметка и балл", 400, attendanceOnly ? 270 : column.Kind == JournalColumnKind.Lesson && !scoreOnly ? 330 : 220);
        Build(cell.Score, column.Kind == JournalColumnKind.Lesson && !scoreOnly, !attendanceOnly);
    }

    public GradeEditor(ApiClient api, int groupId, string subject, int studentId, string studentName, int? final)
    {
        this.api = api;
        this.groupId = groupId;
        this.subject = subject;
        this.studentId = studentId;
        this.studentName = studentName;
        shell = new DialogShell("Итоговый балл", 400, 220);
        Build(final, false);
    }

    public event Action? Saved;

    private void Build(int? initialScore, bool attendance, bool scoreEnabled = true)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, BackColor = Theme.Background };
        layout.Controls.Add(Ui.Label(studentName, 10, Theme.Muted));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        if (attendance)
        {
            var buttons = Ui.Row();
            foreach (var (text, value) in new[]
            {
                ("П", AttendanceStatus.Present), ("Н", AttendanceStatus.Absent), ("Н — уважительная…", AttendanceStatus.Excused)
            })
            {
                var button = new ModernButton { Text = text, Width = value == AttendanceStatus.Excused ? 172 : 62, Height = 38, Selected = status == value };
                button.Click += async (_, _) => await SaveStatusAsync(value);
                buttons.Controls.Add(button);
                statusButtons[value] = button;
            }
            layout.Controls.Add(buttons);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
            var clear = new ModernButton { Text = "Снять отметку", Width = 160, Height = 34 };
            clear.Click += async (_, _) => await SaveStatusAsync(null);
            layout.Controls.Add(clear);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            var reasonRow = Ui.Row();
            reasonLabel.Width = 230;
            reasonLabel.Dock = DockStyle.None;
            reasonRow.Controls.Add(reasonLabel);
            var change = new LinkLabel { Text = "изменить", AutoSize = true, LinkColor = Theme.AccentHover, BackColor = Theme.Background };
            change.Click += async (_, _) => await SaveStatusAsync(AttendanceStatus.Excused);
            reasonRow.Controls.Add(change);
            layout.Controls.Add(reasonRow);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            UpdateReason();
        }
        if (scoreEnabled)
        {
            layout.Controls.Add(Ui.Label("Балл (1–100)", 9, Theme.Muted));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            score.Text = initialScore?.ToString() ?? "";
            score.Dock = DockStyle.Fill;
            score.TextChanged += (_, _) => { score.BackColor = Theme.SurfaceHover; error.Text = ""; };
            score.KeyDown += async (_, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                await SaveScoreAsync();
            };
            layout.Controls.Add(score);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            save.Click += async (_, _) => await SaveScoreAsync();
            shell.Buttons.Controls.Add(save);
            shell.AcceptButton = save;
        }
        layout.Controls.Add(error);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        shell.Content.Controls.Add(layout);
        cancel = shell.AddButton(scoreEnabled ? "Отмена" : "Закрыть", DialogResult.Cancel);
        shell.FormClosing += (_, e) =>
        {
            if (!saving) return;
            e.Cancel = true;
            shell.DialogResult = DialogResult.None;
        };
    }

    private void UpdateReason()
    {
        reasonLabel.Text = status == AttendanceStatus.Excused
            ? UiFormat.ExcuseReasonText(reason ?? ExcuseReason.Other) + (hasDocument ? " · документ" : "")
            : "";
    }

    private async Task SaveStatusAsync(AttendanceStatus? next)
    {
        if (saving || column is null) return;
        if (next == AttendanceStatus.Excused)
        {
            using var dialog = new ExcuseDialog(studentName, reason, hasDocument, comment);
            if (dialog.ShowDialog(shell) != DialogResult.OK) return;
            (reason, hasDocument, comment) = dialog.Result;
        }
        saving = true; error.Text = "";
        save.Enabled = false;
        cancel.Enabled = false;
        try
        {
            await api.MarkAsync(column.Id, column.Date, studentId, next,
                next == AttendanceStatus.Excused ? reason : null,
                next == AttendanceStatus.Excused ? comment : null,
                next == AttendanceStatus.Excused && hasDocument);
            status = next;
            foreach (var (value, button) in statusButtons) button.Selected = value == status;
            UpdateReason();
            Saved?.Invoke();
        }
        catch (ApiException ex) { error.Text = ex.Message; }
        finally { saving = false; if (!shell.IsDisposed) save.Enabled = cancel.Enabled = true; }
    }

    private async Task SaveScoreAsync()
    {
        if (saving) return;
        var value = score.Text.Trim();
        if (value.Length > 0 && (!int.TryParse(value, out var number) || number is < 1 or > 100))
        {
            score.BackColor = Theme.Mix(Theme.SurfaceHover, Theme.Red, .35f);
            error.Text = "Укажите балл от 1 до 100.";
            return;
        }
        int? grade = value.Length == 0 ? null : int.Parse(value);
        saving = true; save.Enabled = false; error.Text = "";
        cancel.Enabled = false;
        try
        {
            if (column?.Kind == JournalColumnKind.Lesson) await api.LessonGradeAsync(column.Id, column.Date, studentId, grade);
            else if (column?.Kind == JournalColumnKind.Assessment) await api.AssessmentGradeAsync(column.Id, studentId, grade);
            else await api.FinalGradeAsync(groupId, subject!, studentId, grade);
            Saved?.Invoke();
            saving = false;
            shell.Close();
        }
        catch (ApiException ex) { error.Text = ex.Message; }
        finally { saving = false; if (!shell.IsDisposed) save.Enabled = cancel.Enabled = true; }
    }

    public DialogResult ShowDialog(IWin32Window? owner, Point near)
    {
        var screen = Screen.FromPoint(near).WorkingArea;
        shell.StartPosition = FormStartPosition.Manual;
        shell.Location = new Point(Math.Clamp(near.X, screen.Left, screen.Right - shell.Width),
            Math.Clamp(near.Y, screen.Top, screen.Bottom - shell.Height));
        return shell.ShowDialog(owner);
    }

    public void Dispose() => shell.Dispose();
}
