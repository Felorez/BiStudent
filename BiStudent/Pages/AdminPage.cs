using System.Globalization;
using System.Runtime.InteropServices;
using BiStudent.Contracts;

namespace BiStudent;

internal sealed class AdminPage : Page
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private readonly DataGridView grid = Ui.Grid();
    private readonly ContextMenuStrip menu = new() { BackColor = Theme.SurfaceHover, ForeColor = Theme.Text, Font = Theme.Font(10) };
    private readonly ToolStripMenuItem editItem = new("Изменить…");
    private readonly ToolStripMenuItem resetItem = new("Сбросить пароль…");
    private readonly ToolStripMenuItem toggleItem = new();

    public AdminPage(ApiClient api) : base(api, "Администрирование", "Учётные записи преподавателей и резервные копии")
    {
        var add = new ModernButton { Text = "Добавить преподавателя", Primary = true, Width = 225 };
        var backups = new ModernButton { Text = "Резервные копии", Width = 175 };
        Actions.Controls.Add(backups);
        Actions.Controls.Add(add);
        add.Click += async (_, _) => await AddAsync();
        backups.Click += (_, _) => { using var dialog = new BackupDialog(Api); dialog.ShowDialog(FindForm()); };

        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "ФИО", FillWeight = 30, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Логин", FillWeight = 20, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Роль", FillWeight = 18, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Статус", FillWeight = 20, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Последний вход", FillWeight = 20, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.CellMouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0)
                grid.CurrentCell = grid.Rows[e.RowIndex].Cells[0];
        };
        grid.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex >= 0 && grid.Rows[e.RowIndex].Tag is TeacherAccountDto teacher)
                await EditAsync(teacher);
        };
        menu.Items.AddRange([editItem, resetItem, toggleItem]);
        menu.Opening += (_, e) =>
        {
            if (SelectedTeacher() is not { } teacher) { e.Cancel = true; return; }
            toggleItem.Text = teacher.IsActive ? "Отключить" : "Включить";
        };
        editItem.Click += async (_, _) => { if (SelectedTeacher() is { } teacher) await EditAsync(teacher); };
        resetItem.Click += async (_, _) => { if (SelectedTeacher() is { } teacher) await ResetAsync(teacher); };
        toggleItem.Click += async (_, _) => { if (SelectedTeacher() is { } teacher) await ToggleAsync(teacher); };
        grid.ContextMenuStrip = menu;

        var card = new CardPanel("Преподаватели", "правый клик по строке — действия") { Dock = DockStyle.Fill };
        card.Body.Controls.Add(grid);
        Body.Controls.Add(card);
    }

    public override Task LoadAsync() => RunAsync(ReloadAsync);

    private TeacherAccountDto? SelectedTeacher() => grid.CurrentRow?.Tag as TeacherAccountDto;

    private async Task ReloadAsync()
    {
        var teachers = await Api.TeachersAsync();
        if (IsDisposed) return;
        grid.Rows.Clear();
        foreach (var teacher in teachers)
        {
            var status = !teacher.IsActive ? "Отключена" : teacher.MustChangePassword ? "Ждёт смены пароля" : "Активна";
            var row = grid.Rows[grid.Rows.Add(
                teacher.FullName, teacher.Login, teacher.IsAdmin ? "Администратор" : "Преподаватель",
                status, teacher.LastLoginAt?.ToString("dd.MM.yyyy HH:mm", Ru) ?? "—")];
            row.Tag = teacher;
            row.Cells[3].Style.ForeColor = !teacher.IsActive ? Theme.Red : teacher.MustChangePassword ? Theme.Yellow : Theme.Green;
        }
        grid.ClearSelection();
    }

    private async Task AddAsync()
    {
        using var dialog = new TeacherDialog(Api, null);
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        if (dialog.TemporaryPassword is { } password) ShowTemporaryPassword(password);
        await ReloadWithToastAsync();
    }

    private async Task EditAsync(TeacherAccountDto teacher)
    {
        using var dialog = new TeacherDialog(Api, teacher);
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
            await RunAsync(ReloadAsync, "Данные преподавателя обновлены");
    }

    private async Task ResetAsync(TeacherAccountDto teacher)
    {
        if (!Ui.Confirm(FindForm(), "Сбросить пароль",
            $"Сбросить пароль преподавателя {teacher.FullName}?", "Сбросить", danger: true)) return;
        TemporaryPasswordDto password;
        try { password = await Api.ResetTeacherPasswordAsync(teacher.Id); }
        catch (ApiException ex) { if (!IsDisposed) Ui.Toast(this, ex.Message, true); return; }
        if (IsDisposed) return;
        ShowTemporaryPassword(password);
        await ReloadWithToastAsync();
    }

    private async Task ReloadWithToastAsync()
    {
        try { await ReloadAsync(); }
        catch (ApiException ex)
        {
            if (!IsDisposed) Ui.Toast(this, ex.Message, true);
        }
    }

    private async Task ToggleAsync(TeacherAccountDto teacher)
    {
        var action = teacher.IsActive ? "Отключить" : "Включить";
        if (!Ui.Confirm(FindForm(), action + " учётную запись",
            $"{action} учётную запись преподавателя {teacher.FullName}?", action, danger: teacher.IsActive)) return;
        await RunAsync(async () =>
        {
            await Api.UpdateTeacherAsync(teacher.Id,
                new SaveTeacherRequest(teacher.Login, teacher.FullName, teacher.IsAdmin, !teacher.IsActive));
            await ReloadAsync();
        }, teacher.IsActive ? "Учётная запись отключена" : "Учётная запись включена");
    }

    private void ShowTemporaryPassword(TemporaryPasswordDto password)
    {
        using var dialog = new DialogShell("Временный пароль", 600, 300);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, BackColor = Theme.Background };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(Ui.Label($"Логин: {password.Login}", 10, Theme.Text), 0, 0);
        var value = Ui.Label(password.Password, 22, Theme.Text, FontStyle.Bold);
        value.Font = new Font(FontFamily.GenericMonospace, 22, FontStyle.Bold);
        layout.Controls.Add(value, 0, 1);
        layout.Controls.Add(Ui.Label("Передайте пароль преподавателю лично. При первом входе система попросит его сменить.",
            9.5f, Theme.Muted), 0, 2);
        dialog.Content.Controls.Add(layout);
        var copy = new ModernButton { Text = "Скопировать", Width = 145 };
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(password.Password);
                Ui.Toast(dialog, "Пароль скопирован");
            }
            catch (ExternalException) { Ui.Toast(dialog, "Не удалось скопировать — буфер обмена занят", true); }
        };
        dialog.Buttons.Controls.Add(copy);
        dialog.AddButton("Закрыть", DialogResult.OK, primary: true);
        dialog.ShowDialog(FindForm());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) menu.Dispose();
        base.Dispose(disposing);
    }
}
