namespace BiStudent;

internal sealed class ChangePasswordDialog : IDisposable
{
    private readonly ApiClient api;
    private readonly DialogShell dialog;
    private readonly TextBox current = Ui.Input("");
    private readonly TextBox next = Ui.Input("");
    private readonly TextBox repeat = Ui.Input("");
    private readonly Label error = Ui.Label("", 9, Theme.Red);
    private readonly ModernButton save = new() { Text = "Сменить пароль", Primary = true, Width = 180 };
    private readonly ModernButton cancel;
    private bool saving;

    public ChangePasswordDialog(ApiClient api, bool forced)
    {
        this.api = api;
        dialog = new DialogShell(forced ? "Смена временного пароля" : "Смена пароля", 540, 470);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 9, BackColor = Theme.Background };
        foreach (var height in new[] { 52, 27, 40, 27, 40, 27, 40, 27, 38 })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        current.UseSystemPasswordChar = true;
        next.UseSystemPasswordChar = true;
        repeat.UseSystemPasswordChar = true;
        current.Dock = DockStyle.Fill;
        next.Dock = DockStyle.Fill;
        repeat.Dock = DockStyle.Fill;
        layout.Controls.Add(Ui.Label(forced
            ? "Администратор выдал временный пароль. Задайте свой, чтобы продолжить."
            : "Укажите текущий и новый пароль.", 9.5f, Theme.Muted), 0, 0);
        layout.Controls.Add(Ui.Label("Текущий пароль", 9.5f, Theme.Muted), 0, 1);
        layout.Controls.Add(current, 0, 2);
        layout.Controls.Add(Ui.Label("Новый пароль", 9.5f, Theme.Muted), 0, 3);
        layout.Controls.Add(next, 0, 4);
        layout.Controls.Add(Ui.Label("Повторите новый пароль", 9.5f, Theme.Muted), 0, 5);
        layout.Controls.Add(repeat, 0, 6);
        layout.Controls.Add(Ui.Label("Не короче 8 символов", 9, Theme.Muted), 0, 7);
        layout.Controls.Add(error, 0, 8);
        dialog.Content.Controls.Add(layout);
        cancel = dialog.AddButton("Отмена", DialogResult.Cancel);
        dialog.Buttons.Controls.Add(save);
        dialog.AcceptButton = save;
        save.Click += async (_, _) => await SaveAsync();
        dialog.Shown += (_, _) => current.Focus();
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
        if (next.Text.Length < 8) { error.Text = "Новый пароль должен содержать не менее 8 символов."; return; }
        if (next.Text != repeat.Text) { error.Text = "Новые пароли не совпадают."; return; }
        saving = true;
        save.Enabled = false;
        cancel.Enabled = false;
        dialog.UseWaitCursor = true;
        error.Text = "";
        try
        {
            await api.ChangePasswordAsync(current.Text, next.Text);
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
