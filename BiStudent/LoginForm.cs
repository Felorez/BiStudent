using System.Net.Http;

namespace BiStudent;

internal sealed class LoginForm : Form
{
    private readonly TextBox password = FormStyle.Input("Введите пароль");
    private readonly Label feedback = FormStyle.Caption("");
    private readonly ModernButton signIn = new() { Text = "Войти", Primary = true, Dock = DockStyle.Fill };
    private readonly CancellationTokenSource cancellation = new();
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly FirebaseSettings? settings;
    private bool busy;
    private bool resourcesDisposed;
    public TeacherSession? Session { get; private set; }
    private readonly CheckBox remember = new() { Text = "Запомнить устройство", Checked = true, AutoSize = true, FlatStyle = FlatStyle.Flat, ForeColor = Theme.Muted, Font = Theme.Font(9) };

    public LoginForm(bool restore = true)
    {
        FormStyle.Dialog(this, "BiStudent · Вход", new Size(510, 540));
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        string? configurationError = null;
        try { settings = FirebaseSettings.Load(); }
        catch { configurationError = "Не удалось прочитать firebase.json рядом с приложением."; }
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 9, Padding = new Padding(36), BackColor = Theme.Background };
        foreach (var height in new[] { 58, 44, 40, 26, 42, 28, 48, 58 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var brand = FormStyle.Caption("BiStudent"); brand.Font = Theme.Font(25, FontStyle.Bold); brand.ForeColor = Theme.Text;
        layout.Controls.Add(brand, 0, 0);
        var name = FormStyle.Caption(settings?.TeacherName ?? "Ниязова Мадина Ержанкызы"); name.Font = Theme.Font(13, FontStyle.Bold); name.ForeColor = Theme.Text;
        layout.Controls.Add(name, 0, 1);
        var email = FormStyle.Caption(settings?.TeacherEmail ?? ""); email.Font = Theme.Font(10); layout.Controls.Add(email, 0, 2);
        layout.Controls.Add(FormStyle.Caption("ПАРОЛЬ"), 0, 3);
        password.UseSystemPasswordChar = true; layout.Controls.Add(password, 0, 4);
        var reveal = new CheckBox { Text = "Показать пароль", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, ForeColor = Theme.Muted, Font = Theme.Font(9) };
        reveal.CheckedChanged += (_, _) => password.UseSystemPasswordChar = !reveal.Checked;
        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false }; reveal.Dock = DockStyle.None; reveal.AutoSize = true;
        options.Controls.Add(reveal); options.Controls.Add(remember); layout.Controls.Add(options, 0, 5);
        layout.Controls.Add(signIn, 0, 6);
        feedback.ForeColor = Theme.Red; feedback.TextAlign = ContentAlignment.MiddleLeft; feedback.Text = configurationError ?? "";
        layout.Controls.Add(feedback, 0, 7);
        var hint = FormStyle.Caption("Вход через Firebase Authentication\nДля входа требуется интернет."); hint.Font = Theme.Font(9);
        layout.Controls.Add(hint, 0, 8);
        Controls.Add(layout);
        signIn.Enabled = settings is not null;
        signIn.Click += async (_, _) => await SignInAsync();
        password.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await SignInAsync(); } };
        Shown += async (_, _) => {
            password.Focus();
            if (!restore || settings is null || RememberedSession.Load(settings) is not string refresh) return;
            busy = true; signIn.Enabled = password.Enabled = false; feedback.Text = "Восстанавливаем вход на этом устройстве…"; feedback.ForeColor = Theme.Muted;
            try { var session = await new FirebaseAuth(settings,http).RestoreAsync(refresh,cancellation.Token); if (IsDisposed || cancellation.IsCancellationRequested) return; RememberedSession.Save(settings,session); Session = session; DialogResult = DialogResult.OK; Close(); }
            catch (InvalidOperationException ex) { RememberedSession.Forget(); if (!IsDisposed) ShowError(ex.Message); }
            catch (Exception) { if (!IsDisposed && !cancellation.IsCancellationRequested) ShowError("Не удалось восстановить вход. Проверьте интернет или введите пароль."); }
            finally { busy = false; if (!IsDisposed) signIn.Enabled = password.Enabled = true; }
        };
    }

    private async Task SignInAsync()
    {
        if (busy || settings is null) return;
        busy = true; signIn.Enabled = false; password.Enabled = false;
        feedback.ForeColor = Theme.Muted; feedback.Text = "Проверяем учётную запись…";
        try
        {
            var session = await new FirebaseAuth(settings, http).SignInAsync(password.Text, cancellation.Token);
            if (IsDisposed || cancellation.IsCancellationRequested) return;
            if (remember.Checked) RememberedSession.Save(settings,session); else RememberedSession.Forget();
            Session = session; password.Clear(); DialogResult = DialogResult.OK; Close();
        }
        catch (OperationCanceledException) { if (!IsDisposed && !cancellation.IsCancellationRequested) ShowError("Firebase не ответил вовремя. Попробуйте ещё раз."); }
        catch (HttpRequestException) { if (!IsDisposed) ShowError("Нет соединения с Firebase. Проверьте интернет."); }
        catch (Exception ex) { if (!IsDisposed) ShowError(ex is InvalidOperationException ? ex.Message : "Не удалось выполнить вход. Попробуйте ещё раз."); }
        finally { busy = false; if (!IsDisposed) { signIn.Enabled = true; password.Enabled = true; } }
    }

    private void ShowError(string message) { feedback.ForeColor = Theme.Red; feedback.Text = message; password.Focus(); }
    protected override void OnFormClosing(FormClosingEventArgs e) { base.OnFormClosing(e); if (!e.Cancel) cancellation.Cancel(); }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !resourcesDisposed) { resourcesDisposed = true; cancellation.Cancel(); http.Dispose(); cancellation.Dispose(); }
        base.Dispose(disposing);
    }
}
