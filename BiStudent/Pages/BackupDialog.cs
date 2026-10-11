using System.Globalization;
using BiStudent.Contracts;

namespace BiStudent;

internal sealed class BackupDialog : DarkForm
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private readonly ApiClient api;
    private readonly DataGridView grid = Ui.Grid();
    private readonly Label error = Ui.Label("", 9.5f, Theme.Red);
    private readonly ModernButton create = new() { Text = "Создать копию сейчас", Primary = true, Width = 188 };
    private readonly ModernButton download = new() { Text = "Скачать…", Width = 112 };
    private readonly ModernButton restore = new() { Text = "Восстановить из файла…", Width = 204 };
    private readonly ModernButton close = new() { Text = "Закрыть", Width = 100 };
    private bool busy;

    public BackupDialog(ApiClient api)
    {
        this.api = api;
        Text = "Резервные копии";
        ClientSize = new Size(760, 560);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        var heading = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background };
        var title = Ui.Label("Резервные копии", 16, Theme.Text, FontStyle.Bold);
        title.Dock = DockStyle.Top;
        title.Height = 42;
        var hint = Ui.Label("Сервер сам делает копию раз в сутки и хранит последние 7. Копия содержит ключи шифрования образцов лиц — храните файл как пароль.", 9.5f, Theme.Muted);
        hint.TextAlign = ContentAlignment.TopLeft;
        heading.Controls.Add(hint);
        heading.Controls.Add(title);

        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Дата", FillWeight = 45, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Тип", FillWeight = 25, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Размер", FillWeight = 25, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.SelectionChanged += (_, _) => download.Enabled = !busy && SelectedBackup() is not null;
        error.TextAlign = ContentAlignment.MiddleLeft;

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = Theme.Background, Padding = new Padding(0, 10, 0, 0) };
        buttons.Controls.Add(close);
        buttons.Controls.Add(restore);
        buttons.Controls.Add(download);
        buttons.Controls.Add(create);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(24, 16, 24, 16), BackColor = Theme.Background };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 102));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.Controls.Add(heading, 0, 0);
        layout.Controls.Add(grid, 0, 1);
        layout.Controls.Add(error, 0, 2);
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);

        create.Click += async (_, _) => await RunAsync(async () =>
        {
            await api.CreateBackupAsync();
            await LoadAsync();
            Ui.Toast(this, "Копия создана");
        });
        download.Click += async (_, _) => await RunAsync(DownloadAsync);
        restore.Click += async (_, _) => await RunAsync(RestoreAsync);
        close.Click += (_, _) => Close();
        Shown += async (_, _) => await RunAsync(LoadAsync);
    }

    private BackupFileDto? SelectedBackup() => grid.SelectedRows.Count == 0 ? null : grid.SelectedRows[0].Tag as BackupFileDto;

    private async Task LoadAsync()
    {
        var files = await api.BackupsAsync();
        grid.Rows.Clear();
        foreach (var file in files)
        {
            var row = grid.Rows[grid.Rows.Add(
                file.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm", Ru),
                file.Automatic ? "авто" : "вручную",
                FormatSize(file.Size))];
            row.Tag = file;
        }
        grid.ClearSelection();
        if (grid.Rows.Count > 0) grid.Rows[0].Selected = true;
        download.Enabled = !busy && SelectedBackup() is not null;
    }

    private static string FormatSize(long size)
    {
        var unit = size >= 1024 * 1024 ? "МБ" : "КБ";
        var value = size >= 1024 * 1024 ? size / (1024d * 1024) : size / 1024d;
        return $"{value.ToString("0.#", Ru)} {unit}";
    }

    private async Task DownloadAsync()
    {
        var selected = SelectedBackup();
        if (selected is null) return;
        var (content, fileName) = await api.DownloadBackupAsync(selected.Name);
        using var dialog = new SaveFileDialog { Filter = "Архив ZIP (*.zip)|*.zip", FileName = Path.GetFileName(fileName), DefaultExt = "zip" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await File.WriteAllBytesAsync(dialog.FileName, content);
        Ui.Toast(this, "Копия сохранена");
    }

    private async Task RestoreAsync()
    {
        using var dialog = new OpenFileDialog { Filter = "Архив ZIP (*.zip)|*.zip" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (!Ui.Confirm(this, "Восстановить данные?",
            "Текущие данные будут заменены данными из копии. Перед заменой сервер сохранит их в отдельную копию.",
            "Восстановить", danger: true)) return;
        var content = await File.ReadAllBytesAsync(dialog.FileName);
        var result = await api.RestoreAsync(content);
        Ui.Message(this, "Восстановление", result.Message + Environment.NewLine + Environment.NewLine +
            "Сейчас приложение вернётся к окну входа.");
        try { await LoadAsync(); }
        catch (ApiException) { }
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (busy) return;
        busy = true;
        SetBusy(true);
        error.Text = "";
        UseWaitCursor = true;
        try { await action(); }
        catch (ApiException ex) { error.Text = ex.Message; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error.Text = ex.Message; }
        finally
        {
            busy = false;
            if (!IsDisposed)
            {
                SetBusy(false);
                UseWaitCursor = false;
            }
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (busy && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
        base.OnFormClosing(e);
    }

    private void SetBusy(bool value)
    {
        create.Enabled = !value;
        download.Enabled = !value && SelectedBackup() is not null;
        restore.Enabled = !value;
        close.Enabled = !value;
    }
}
