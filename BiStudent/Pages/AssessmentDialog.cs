using BiStudent.Contracts;

namespace BiStudent;

internal sealed class AssessmentDialog : IDisposable
{
    private readonly DialogShell shell;
    private readonly ApiClient api;
    private readonly int groupId;
    private readonly string subject;
    private readonly AssessmentDto? assessment;
    private readonly IReadOnlyList<AssessmentDto> existing;
    private readonly ModernComboBox kind = new() { Dock = DockStyle.Fill };
    private readonly TextBox title = Ui.Input("Название мероприятия");
    private readonly DateTimePicker date = new()
    {
        Format = DateTimePickerFormat.Custom, CustomFormat = "dd.MM.yyyy", Dock = DockStyle.Fill,
        Font = Theme.Font(10), CalendarMonthBackground = Theme.Surface, CalendarForeColor = Theme.Text
    };
    private readonly Label error = Ui.Label("", 9, Theme.Red);
    private readonly ModernButton save = new() { Text = "Сохранить", Primary = true };
    private readonly ModernButton cancel;
    private bool edited;
    private bool settingTitle;
    private bool saving;

    public AssessmentDialog(ApiClient api, int groupId, string groupName, string subject,
        IReadOnlyList<AssessmentDto> existing, AssessmentDto? assessment = null)
    {
        this.api = api;
        this.groupId = groupId;
        this.subject = subject;
        this.existing = existing;
        this.assessment = assessment;
        shell = new DialogShell(assessment is null ? "Новое контрольное мероприятие" : "Изменить мероприятие", 540, 420);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 9, BackColor = Theme.Background };
        foreach (var height in new[] { 28, 28, 28, 42, 28, 42, 28, 42, 36 })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.Controls.Add(Ui.Label($"Группа: {groupName}", 10, Theme.Text), 0, 0);
        layout.Controls.Add(Ui.Label($"Дисциплина: {subject}", 10, Theme.Text), 0, 1);
        layout.Controls.Add(Ui.Label("Тип", 9, Theme.Muted), 0, 2);
        foreach (var value in Enum.GetValues<AssessmentKind>()) kind.Items.Add(UiFormat.AssessmentKindText(value));
        kind.SelectedIndex = (int)(assessment?.Kind ?? AssessmentKind.Test);
        layout.Controls.Add(kind, 0, 3);
        layout.Controls.Add(Ui.Label("Название", 9, Theme.Muted), 0, 4);
        title.Dock = DockStyle.Fill;
        title.TextChanged += (_, _) => { if (!settingTitle) edited = true; };
        layout.Controls.Add(title, 0, 5);
        layout.Controls.Add(Ui.Label("Дата", 9, Theme.Muted), 0, 6);
        date.Value = assessment?.Date.ToDateTime(TimeOnly.MinValue) ?? DateTime.Today;
        layout.Controls.Add(date, 0, 7);
        layout.Controls.Add(error, 0, 8);
        shell.Content.Controls.Add(layout);
        if (assessment is null) SetSuggestedTitle();
        else { title.Text = assessment.Title; edited = true; }
        kind.SelectedIndexChanged += (_, _) => { if (!edited) SetSuggestedTitle(); };
        save.Click += async (_, _) => await SaveAsync();
        shell.Buttons.Controls.Add(save);
        cancel = shell.AddButton("Отмена", DialogResult.Cancel);
        if (assessment is not null)
        {
            var delete = new ModernButton { Text = "Удалить", Danger = true };
            delete.Click += async (_, _) => await DeleteAsync();
            shell.Buttons.Controls.Add(delete);
        }
        shell.AcceptButton = save;
        shell.FormClosing += (_, e) =>
        {
            if (!saving) return;
            e.Cancel = true;
            shell.DialogResult = DialogResult.None;
        };
    }

    private void SetSuggestedTitle()
    {
        var selected = (AssessmentKind)kind.SelectedIndex;
        var number = existing.Count(x => x.Kind == selected && x.Id != assessment?.Id) + 1;
        settingTitle = true;
        title.Text = $"{UiFormat.AssessmentKindText(selected)} №{number}";
        settingTitle = false;
    }

    private async Task SaveAsync()
    {
        if (saving) return;
        if (string.IsNullOrWhiteSpace(title.Text)) { error.Text = "Укажите название мероприятия."; return; }
        saving = true; save.Enabled = cancel.Enabled = false; error.Text = "";
        try
        {
            await api.SaveAssessmentAsync(assessment?.Id, new SaveAssessmentRequest(groupId, subject,
                title.Text.Trim(), (AssessmentKind)kind.SelectedIndex, DateOnly.FromDateTime(date.Value)));
            saving = false;
            shell.DialogResult = DialogResult.OK;
            shell.Close();
        }
        catch (ApiException ex) { error.Text = ex.Message; }
        finally { saving = false; if (!shell.IsDisposed) save.Enabled = cancel.Enabled = true; }
    }

    private async Task DeleteAsync()
    {
        if (saving || assessment is null || !Ui.Confirm(shell, "Удалить мероприятие?",
            $"Удалить «{assessment.Title}» и выставленные за него баллы?", "Удалить", danger: true)) return;
        saving = true; save.Enabled = cancel.Enabled = false; error.Text = "";
        try { await api.DeleteAssessmentAsync(assessment.Id); saving = false; shell.DialogResult = DialogResult.OK; shell.Close(); }
        catch (ApiException ex) { error.Text = ex.Message; }
        finally { saving = false; if (!shell.IsDisposed) save.Enabled = cancel.Enabled = true; }
    }

    public DialogResult ShowDialog(IWin32Window? owner) => shell.ShowDialog(owner);
    public void Dispose() => shell.Dispose();
}
