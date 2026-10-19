using BiStudent.Contracts;

namespace BiStudent;

internal sealed class StudentDialog : DarkForm
{
    private readonly ApiClient api;
    private readonly StudentDto? student;
    private readonly IReadOnlyList<GroupDto> groups;
    private readonly TextBox name = Ui.Input("Фамилия Имя Отчество");
    private readonly ModernComboBox group = new() { Dock = DockStyle.Fill };
    private readonly Label error = Ui.Label("", 9, Theme.Red);
    private readonly ModernButton save = new() { Text = "Сохранить", Primary = true };
    private readonly ModernButton cancel = new() { Text = "Отмена" };
    private bool saving;

    public StudentDialog(ApiClient api, IReadOnlyList<GroupDto> groups, StudentDto? student, int? selectedGroupId)
    {
        this.api = api;
        this.groups = groups;
        this.student = student;
        Text = student is null ? "Новый студент" : "Изменить студента";
        ClientSize = new Size(480, 310);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6,
            Padding = new Padding(24, 18, 24, 0), BackColor = Theme.Background
        };
        foreach (var height in new[] { 31, 45, 31, 45, 39, 40 })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        name.Dock = DockStyle.Fill;
        name.Text = student?.FullName ?? "";
        foreach (var item in groups) group.Items.Add(item.Name);
        var selected = selectedGroupId ?? student?.GroupId;
        group.SelectedIndex = selected is { } id ? groups.ToList().FindIndex(g => g.Id == id) : -1;
        if (group.SelectedIndex < 0 && group.Items.Count > 0) group.SelectedIndex = 0;
        error.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(Ui.Label("ФИО", 9.5f, Theme.Muted), 0, 0);
        layout.Controls.Add(name, 0, 1);
        layout.Controls.Add(Ui.Label("Группа", 9.5f, Theme.Muted), 0, 2);
        layout.Controls.Add(group, 0, 3);
        layout.Controls.Add(error, 0, 4);

        var actions = Ui.Row();
        actions.FlowDirection = FlowDirection.RightToLeft;
        actions.Controls.Add(save); actions.Controls.Add(cancel);
        layout.Controls.Add(actions, 0, 5);
        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;
        save.Click += async (_, _) => await SaveAsync();
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Shown += (_, _) => { name.Focus(); name.SelectAll(); };
    }

    private async Task SaveAsync()
    {
        if (saving) return;
        var fullName = name.Text.Trim();
        if (fullName.Length == 0 || group.SelectedIndex < 0)
        {
            error.Text = "Укажите ФИО и группу.";
            return;
        }
        saving = true;
        save.Enabled = false;
        cancel.Enabled = false;
        UseWaitCursor = true;
        error.Text = "Сохранение…";
        try
        {
            await api.SaveStudentAsync(student?.Id, new SaveStudentRequest(fullName, groups[group.SelectedIndex].Id));
            if (IsDisposed) return;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (ApiException ex) { error.Text = ex.Message; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { error.Text = ex.Message; }
        finally
        {
            saving = false;
            if (!IsDisposed) { save.Enabled = true; cancel.Enabled = true; UseWaitCursor = false; }
        }
    }
}
