namespace BiStudent;

internal sealed class LoadingForm : Form
{
    public LoadingForm(CloudWorkspace workspace, Func<Task>? afterLoad = null)
    {
        FormStyle.Dialog(this, "BiStudent · Загрузка", new Size(520, 240));
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 12)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        var label = FormStyle.Caption("Загрузка студентов, предметов и оценок…"); label.Font = Theme.Font(13); layout.Controls.Add(label, 0, 0);
        var progress = new ProgressBar { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee }; layout.Controls.Add(progress, 0, 1);
        var retry = new ModernButton { Text = "Повторить загрузку", Dock = DockStyle.Fill, Visible = false }; layout.Controls.Add(retry, 0, 2); Controls.Add(layout);
        async Task Load()
        {
            retry.Visible = false; progress.Visible = true;
            try { await workspace.LoadAsync(); if (afterLoad is not null) { label.Text="Синхронизация образцов лиц…"; await afterLoad(); } if (!IsDisposed) { DialogResult = DialogResult.OK; Close(); } }
            catch (Exception ex) { if (!IsDisposed) { label.Text = ex is InvalidOperationException ? ex.Message : "Не удалось загрузить данные. Проверьте интернет."; retry.Visible = true; progress.Visible = false; } }
        }
        Shown += async (_, _) => await Load(); retry.Click += async (_, _) => await Load();
    }
}

internal sealed class AutoSaveSettingsForm : Form
{
    public int Seconds { get; private set; }
    public AutoSaveSettingsForm(int seconds)
    {
        FormStyle.Dialog(this, "Настройки автосохранения", new Size(480, 260));
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(FormStyle.Caption("Интервал автосохранения · от 1 до 3600 секунд"), 0, 0);
        var value = FormStyle.Input("Секунды"); value.Text = seconds.ToString(); layout.Controls.Add(value, 0, 1);
        var save = new ModernButton { Text = "Применить", Primary = true, Dock = DockStyle.Fill };
        save.Click += (_, _) => { if (!int.TryParse(value.Text, out var parsed) || parsed is < 1 or > 3600) { MessageBox.Show(this, "Введите число от 1 до 3600."); return; } Seconds = parsed; DialogResult = DialogResult.OK; Close(); };
        layout.Controls.Add(save, 0, 2); Controls.Add(layout);
    }
}
