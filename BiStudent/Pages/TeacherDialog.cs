using BiStudent.Contracts;

namespace BiStudent;

internal sealed class TeacherDialog : IDisposable
{
    private readonly ApiClient api;
    private readonly TeacherAccountDto? teacher;
    private readonly DialogShell dialog;
    private readonly TextBox name = Ui.Input("Фамилия Имя Отчество");
    private readonly TextBox login = Ui.Input("Логин");
    private readonly CheckBox admin = new() { Text = "Администратор", ForeColor = Theme.Text, FlatStyle = FlatStyle.Flat, Dock = DockStyle.Fill };
    private readonly CheckBox active = new() { Text = "Учётная запись активна", ForeColor = Theme.Text, FlatStyle = FlatStyle.Flat, Dock = DockStyle.Fill };
    private readonly Label error = Ui.Label("", 9, Theme.Red);
    private readonly ModernButton save = new() { Text = "Сохранить", Primary = true };
    private readonly ModernButton cancel;
    private bool saving;

    public TemporaryPasswordDto? TemporaryPassword { get; private set; }

    public TeacherDialog(ApiClient api, TeacherAccountDto? teacher)
    {
        this.api = api;
        this.teacher = teacher;
        dialog = new DialogShell(teacher is null ? "Новый преподаватель" : "Изменить преподавателя", 520, 410);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, BackColor = Theme.Background };
        foreach (var height in new[] { 28, 42, 28, 42, 42, 42, 40 })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        name.Text = teacher?.FullName ?? "";
        login.Text = teacher?.Login ?? "";
        admin.Checked = teacher?.IsAdmin ?? false;
        active.Checked = teacher?.IsActive ?? true;
        name.Dock = DockStyle.Fill;
        login.Dock = DockStyle.Fill;
        layout.Controls.Add(Ui.Label("ФИО", 9.5f, Theme.Muted), 0, 0);
        layout.Controls.Add(name, 0, 1);
        layout.Controls.Add(Ui.Label("Логин", 9.5f, Theme.Muted), 0, 2);
        layout.Controls.Add(login, 0, 3);
        layout.Controls.Add(admin, 0, 4);
        layout.Controls.Add(active, 0, 5);
        layout.Controls.Add(error, 0, 6);
        dialog.Content.Controls.Add(layout);
        cancel = dialog.AddButton("Отмена", DialogResult.Cancel);
        dialog.Buttons.Controls.Add(save);
        dialog.AcceptButton = save;
        save.Click += async (_, _) => await SaveAsync();
        dialog.Shown += (_, _) => { name.Focus(); name.SelectAll(); };
        dialog.FormClosing += (_, e) =>
        {
            if (!saving) return;
            e.Cancel = true;
            dialog.DialogResult = DialogResult.None;
        };
    }

    public DialogResult ShowDialog(IWin32Window? owner = null) => dialog.ShowDialog(owner);

    private async Task SaveAsync()
    {
        if (saving) return;
        var fullName = name.Text.Trim();
        var userLogin = login.Text.Trim();
        if (fullName.Length == 0 || userLogin.Length == 0)
        {
            error.Text = "Укажите ФИО и логин.";
            return;
        }
        saving = true;
        save.Enabled = false;
        cancel.Enabled = false;
        dialog.UseWaitCursor = true;
        error.Text = "";
        try
        {
            var request = new SaveTeacherRequest(userLogin, fullName, admin.Checked, active.Checked);
            if (teacher is null) TemporaryPassword = await api.CreateTeacherAsync(request);
            else await api.UpdateTeacherAsync(teacher.Id, request);
            if (dialog.IsDisposed) return;
            saving = false;
            dialog.DialogResult = DialogResult.OK;
            dialog.Close();
        }
        catch (ApiException ex) { error.Text = ex.Message; }
        finally
        {
            saving = false;
            if (!dialog.IsDisposed) { save.Enabled = cancel.Enabled = true; dialog.UseWaitCursor = false; }
        }
    }

    public void Dispose() => dialog.Dispose();
}
