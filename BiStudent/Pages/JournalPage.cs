using System.Diagnostics;
using BiStudent.Contracts;

namespace BiStudent;

internal sealed class JournalPage : Page
{
    private readonly ModernComboBox groups = new() { Width = 160, DisplayMember = nameof(GroupDto.Name) };
    private readonly ModernComboBox subjects = new() { Width = 190 };
    private readonly DateTimePicker from = Picker();
    private readonly DateTimePicker to = Picker();
    private readonly ModernButton week = new() { Text = "Неделя", Width = 95 };
    private readonly ModernButton month = new() { Text = "Месяц", Width = 95 };
    private readonly ModernButton semester = new() { Text = "Семестр", Width = 105 };
    private readonly ModernButton csv = new() { Text = "CSV", Width = 90 };
    private readonly ModernButton excel = new() { Text = "Скачать Excel", Primary = true, Width = 170 };
    private readonly DataGridView grid = Ui.Grid();
    private readonly CardPanel card = new("Журнал посещаемости", "клик по отметке — изменить; двойной клик по дате — открыть занятие");
    private readonly Label empty = Ui.Label("За период занятий нет", 12, Theme.Muted);
    private JournalDto? journal;
    private JournalFilter? loadedFilter;
    private bool busy;
    private bool changing;

    public JournalPage(ApiClient api) : base(api, "Журнал посещаемости", "Собирается автоматически из отметок камеры и ручных правок")
    {
        csv.Click += async (_, _) => await ExportAsync("csv");
        excel.Click += async (_, _) => await ExportAsync("xlsx");
        Actions.Controls.Add(csv); Actions.Controls.Add(excel);
        var hint = new AutomationHint("журнал за месяц раньше сводился вручную около 20 минут — теперь он готов в любой момент, а Excel выгружается одной кнопкой");
        var filterCard = new SmoothPanel { Dock = DockStyle.Top, Height = 116, Padding = new Padding(16, 8, 8, 8) };
        var filters = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, BackColor = Color.Transparent };
        filters.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); filters.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        var fields = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent };
        fields.Controls.Add(FilterLabel("Группа")); fields.Controls.Add(groups);
        fields.Controls.Add(FilterLabel("Предмет")); fields.Controls.Add(subjects);
        fields.Controls.Add(FilterLabel("С")); fields.Controls.Add(from);
        fields.Controls.Add(FilterLabel("По")); fields.Controls.Add(to);
        var periods = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent };
        periods.Controls.Add(week); periods.Controls.Add(month); periods.Controls.Add(semester);
        filters.Controls.Add(fields, 0, 0); filters.Controls.Add(periods, 0, 1);
        filterCard.Controls.Add(filters);
        groups.SelectedIndexChanged += async (_, _) => { if (!changing) await ExecuteAsync(() => RefreshAsync(true)); };
        subjects.SelectedIndexChanged += async (_, _) => { if (!changing) await ExecuteAsync(() => RefreshAsync(false)); };
        from.ValueChanged += async (_, _) => await DatesChangedAsync(true);
        to.ValueChanged += async (_, _) => await DatesChangedAsync(false);
        week.Click += async (_, _) => await PresetAsync(week, UiFormat.Monday(DateOnly.FromDateTime(DateTime.Today)).ToDateTime(TimeOnly.MinValue), UiFormat.Monday(DateOnly.FromDateTime(DateTime.Today)).AddDays(6).ToDateTime(TimeOnly.MinValue));
        month.Click += async (_, _) => await PresetAsync(month, new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1), new DateTime(DateTime.Today.Year, DateTime.Today.Month, DateTime.DaysInMonth(DateTime.Today.Year, DateTime.Today.Month)));
        semester.Click += async (_, _) => await PresetAsync(semester, SemesterStart(DateTime.Today), DateTime.Today);
        changing = true;
        from.Value = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        to.Value = DateTime.Today;
        month.Selected = true;
        changing = false;

        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        grid.ColumnHeadersHeight = 58;
        grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
        grid.CellClick += async (_, e) => await MarkCellAsync(e.RowIndex, e.ColumnIndex);
        grid.CellPainting += PaintStatus;
        grid.ColumnHeaderMouseDoubleClick += async (_, e) => await OpenLessonAsync(e.ColumnIndex);
        empty.TextAlign = ContentAlignment.MiddleCenter;
        card.Dock = DockStyle.Fill;
        var legend = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, FlowDirection = FlowDirection.LeftToRight, BackColor = Theme.Surface, Padding = new Padding(8, 8, 0, 0) };
        foreach (var status in new AttendanceStatus?[] { AttendanceStatus.Present, AttendanceStatus.Late, AttendanceStatus.Absent, AttendanceStatus.Excused, null })
        {
            var item = Ui.Label($"{UiFormat.StatusSymbol(status)}  {UiFormat.StatusText(status)}", 9, UiFormat.StatusColor(status));
            item.Dock = DockStyle.None; item.AutoSize = true; item.Margin = new Padding(0, 0, 22, 0);
            legend.Controls.Add(item);
        }
        card.Body.Controls.Add(grid); card.Body.Controls.Add(empty); card.Body.Controls.Add(legend);
        Body.Controls.Add(card);
        Body.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 16, BackColor = Theme.Background });
        Body.Controls.Add(filterCard);
        Body.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 16, BackColor = Theme.Background });
        Body.Controls.Add(hint);
        SetBusy(false);
    }

    private static DateTimePicker Picker() => new()
    {
        Format = DateTimePickerFormat.Custom, CustomFormat = "dd.MM.yyyy", Width = 115,
        Font = Theme.Font(10), CalendarMonthBackground = Theme.Surface, CalendarForeColor = Theme.Text,
        Margin = new Padding(0, 5, 10, 0)
    };

    private static Label FilterLabel(string text) => new()
    {
        Text = text, AutoSize = true, Font = Theme.Font(9), ForeColor = Theme.Muted,
        BackColor = Color.Transparent, Margin = new Padding(0, 12, 7, 0)
    };

    private static DateTime SemesterStart(DateTime today) => today.Month switch
    {
        >= 9 => new DateTime(today.Year, 9, 1),
        >= 2 => new DateTime(today.Year, 2, 1),
        _ => new DateTime(today.Year - 1, 9, 1)
    };

    public override async Task LoadAsync()
    {
        await ExecuteAsync(async () =>
        {
            var selectedId = (groups.SelectedItem as GroupDto)?.Id;
            var items = await Api.GroupsAsync();
            if (IsDisposed) return;
            changing = true;
            groups.Items.Clear();
            foreach (var item in items.Where(x => x.Students > 0)) groups.Items.Add(item);
            groups.SelectedItem = groups.Items.Cast<GroupDto>().FirstOrDefault(x => x.Id == selectedId) ?? groups.Items.Cast<GroupDto>().FirstOrDefault();
            changing = false;
            await RefreshAsync(true);
        });
    }

    private async Task DatesChangedAsync(bool start)
    {
        if (changing) return;
        changing = true;
        if (from.Value.Date > to.Value.Date)
        {
            if (start) to.Value = from.Value.Date;
            else from.Value = to.Value.Date;
        }
        week.Selected = month.Selected = semester.Selected = false;
        changing = false;
        await ExecuteAsync(() => RefreshAsync(true));
    }

    private async Task PresetAsync(ModernButton chosen, DateTime start, DateTime end)
    {
        if (busy) return;
        changing = true;
        from.Value = start; to.Value = end;
        week.Selected = chosen == week; month.Selected = chosen == month; semester.Selected = chosen == semester;
        changing = false;
        await ExecuteAsync(() => RefreshAsync(true));
    }

    private string? SelectedSubject => subjects.SelectedIndex > 0 ? subjects.SelectedItem as string : null;
    private JournalFilter? CurrentFilter => groups.SelectedItem is GroupDto group
        ? new(group.Id, DateOnly.FromDateTime(from.Value), DateOnly.FromDateTime(to.Value), SelectedSubject) : null;

    private async Task RefreshAsync(bool updateSubjects, bool preserve = false)
    {
        if (groups.SelectedItem is not GroupDto group)
        {
            ClearJournal("Нет групп со студентами");
            return;
        }
        var start = DateOnly.FromDateTime(from.Value);
        var end = DateOnly.FromDateTime(to.Value);
        try
        {
            JournalDto data;
            var subject = SelectedSubject;
            if (updateSubjects)
            {
                var all = await Api.JournalAsync(group.Id, start, end, null);
                if (IsDisposed) return;
                SetSubjects(all.Columns);
                subject = SelectedSubject;
                data = subject is { } name ? await Api.JournalAsync(group.Id, start, end, name) : all;
            }
            else data = await Api.JournalAsync(group.Id, start, end, subject);
            if (IsDisposed) return;
            ShowJournal(data, preserve);
            loadedFilter = new(group.Id, start, end, subject);
        }
        catch (Exception ex)
        {
            if (!IsDisposed) ClearJournal(ex.Message);
            throw;
        }
    }

    private void ClearJournal(string message)
    {
        journal = null; loadedFilter = null;
        grid.Rows.Clear(); grid.Columns.Clear(); grid.Visible = false;
        empty.Text = message; empty.Visible = true;
    }

    private void SetSubjects(List<JournalColumnDto> columns)
    {
        var selected = SelectedSubject;
        changing = true;
        subjects.Items.Clear();
        subjects.Items.Add("Все предметы");
        foreach (var name in columns.Select(x => x.Subject).Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase))
            subjects.Items.Add(name);
        subjects.SelectedItem = selected is not null && subjects.Items.Contains(selected) ? selected : "Все предметы";
        changing = false;
    }

    private static string ShortSubject(string subject)
    {
        if (subject.Length <= 10) return subject;
        var words = subject.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 1 ? string.Join("", words.Select(x => char.ToUpperInvariant(x[0]))) : subject[..9] + "…";
    }

    private void ShowJournal(JournalDto data, bool preserve)
    {
        var firstRow = preserve ? grid.FirstDisplayedScrollingRowIndex : -1;
        var horizontal = preserve ? grid.HorizontalScrollingOffset : 0;
        journal = data;
        grid.SuspendLayout();
        grid.Rows.Clear(); grid.Columns.Clear();
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "ФИО", Width = 240, Frozen = true, SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 0, 0), Font = Theme.Font(10, FontStyle.Bold) } });
        foreach (var column in data.Columns)
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = $"{column.Date:dd.MM}\n{ShortSubject(column.Subject)}", Width = 90, Tag = column,
                ToolTipText = $"{column.Date:dd.MM.yyyy} · {UiFormat.Time(column.Start)} · {column.Subject}",
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
        }
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Итого", Width = 100, SortMode = DataGridViewColumnSortMode.NotSortable });
        foreach (var student in data.Students)
        {
            var values = new object[data.Columns.Count + 2];
            values[0] = student.FullName;
            for (var i = 0; i < data.Columns.Count; i++) values[i + 1] = UiFormat.StatusSymbol(i < student.Statuses.Count ? student.Statuses[i] : null);
            values[^1] = UiFormat.Percent(student.Percent);
            var row = grid.Rows[grid.Rows.Add(values)];
            row.Tag = student;
            row.Cells[row.Cells.Count - 1].Style.ForeColor = UiFormat.PercentColor(student.Percent);
            row.Cells[row.Cells.Count - 1].Style.Font = Theme.Font(10, FontStyle.Bold);
        }
        grid.ResumeLayout();
        empty.Text = "За период занятий нет";
        empty.Visible = data.Columns.Count == 0;
        grid.Visible = data.Columns.Count > 0;
        if (firstRow >= 0 && grid.Rows.Count > 0) grid.FirstDisplayedScrollingRowIndex = Math.Min(firstRow, grid.Rows.Count - 1);
        if (horizontal > 0) grid.HorizontalScrollingOffset = horizontal;
    }

    private void PaintStatus(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || grid.Columns[e.ColumnIndex].Tag is not JournalColumnDto) return;
        var student = grid.Rows[e.RowIndex].Tag as JournalStudentDto;
        if (student is null) return;
        var index = e.ColumnIndex - 1;
        var status = index < student.Statuses.Count ? student.Statuses[index] : null;
        e.Paint(e.CellBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.Border);
        var color = UiFormat.StatusColor(status);
        var pill = new Rectangle(e.CellBounds.X + (e.CellBounds.Width - 36) / 2, e.CellBounds.Y + (e.CellBounds.Height - 27) / 2, 36, 27);
        Ui.FillRounded(e.Graphics!, pill, 10, Theme.Mix(Theme.Surface, color, .2f));
        TextRenderer.DrawText(e.Graphics!, UiFormat.StatusSymbol(status), Theme.Font(10, FontStyle.Bold), pill, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        e.Handled = true;
    }

    private static AttendanceStatus? Next(AttendanceStatus? current) => current switch
    {
        null => AttendanceStatus.Present,
        AttendanceStatus.Present => AttendanceStatus.Late,
        AttendanceStatus.Late => AttendanceStatus.Absent,
        AttendanceStatus.Absent => AttendanceStatus.Excused,
        _ => null
    };

    private async Task MarkCellAsync(int rowIndex, int columnIndex)
    {
        if (busy || loadedFilter != CurrentFilter || rowIndex < 0 || columnIndex < 0 || grid.Columns[columnIndex].Tag is not JournalColumnDto column
            || grid.Rows[rowIndex].Tag is not JournalStudentDto student) return;
        var current = columnIndex - 1 < student.Statuses.Count ? student.Statuses[columnIndex - 1] : null;
        await ExecuteAsync(async () =>
        {
            await Api.MarkAsync(column.SlotId, column.Date, student.StudentId, Next(current));
            if (!IsDisposed) await RefreshAsync(false, true);
        });
    }

    private async Task OpenLessonAsync(int columnIndex)
    {
        if (busy || loadedFilter != CurrentFilter || columnIndex < 0 || grid.Columns[columnIndex].Tag is not JournalColumnDto column
            || FindForm() is not { } owner) return;
        LessonDto? lesson = null;
        var loaded = false;
        await ExecuteAsync(async () =>
        {
            var lessons = await Api.DayAsync(column.Date);
            if (IsDisposed || owner.IsDisposed) return;
            lesson = lessons.FirstOrDefault(x => x.Slot.Id == column.SlotId);
            loaded = true;
        });
        if (!loaded || IsDisposed || owner.IsDisposed) return;
        if (lesson is null) { Ui.Toast(this, "Занятие не найдено в расписании.", true); return; }
        using (var dialog = new LessonDialog(Api, lesson)) dialog.ShowDialog(owner);
        if (!IsDisposed) await ExecuteAsync(() => RefreshAsync(false, true));
    }

    private async Task ExportAsync(string format)
    {
        if (groups.SelectedItem is not GroupDto group) return;
        await ExecuteAsync(async () =>
        {
            var (content, name) = await Api.JournalFileAsync(group.Id, DateOnly.FromDateTime(from.Value), DateOnly.FromDateTime(to.Value), SelectedSubject, format);
            if (IsDisposed) return;
            using var dialog = new SaveFileDialog
            {
                FileName = name, DefaultExt = format, AddExtension = true,
                Filter = format == "csv" ? "CSV (*.csv)|*.csv" : "Excel (*.xlsx)|*.xlsx"
            };
            if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
            await File.WriteAllBytesAsync(dialog.FileName, content);
            if (!IsDisposed) ShowSavedToast(dialog.FileName);
        });
    }

    private void ShowSavedToast(string path)
    {
        var form = FindForm();
        if (form is null || form.IsDisposed) return;
        var toast = new SmoothPanel { Width = 320, Height = 50, Radius = 12, Padding = new Padding(12, 4, 8, 4), BackColor = Theme.SurfaceAlt };
        var label = Ui.Label("Файл сохранён", 10, Theme.Text, FontStyle.Bold);
        var open = new ModernButton { Text = "Открыть", Width = 100, Dock = DockStyle.Right, Margin = Padding.Empty };
        open.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { Ui.Toast(this, $"Не удалось открыть файл: {ex.Message}", true); }
            if (!toast.IsDisposed) toast.Dispose();
        };
        toast.Controls.Add(label); toast.Controls.Add(open);
        form.Controls.Add(toast);
        toast.Location = new Point(form.ClientSize.Width - toast.Width - 24, form.ClientSize.Height - toast.Height - 24);
        toast.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        toast.BringToFront();
        var timer = new System.Windows.Forms.Timer { Interval = 8000 };
        timer.Tick += (_, _) => { timer.Dispose(); if (!toast.IsDisposed) toast.Dispose(); };
        toast.Disposed += (_, _) => timer.Dispose();
        timer.Start();
    }

    private async Task ExecuteAsync(Func<Task> action)
    {
        if (busy || IsDisposed) return;
        busy = true; SetBusy(true); UseWaitCursor = true;
        try { await action(); }
        catch (ApiException ex) { if (!IsDisposed) Ui.Toast(this, ex.Message, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            if (!IsDisposed) Ui.Toast(this, ex.Message, true);
        }
        finally { busy = false; if (!IsDisposed) { UseWaitCursor = false; SetBusy(false); } }
    }

    private void SetBusy(bool state)
    {
        groups.Enabled = !state && groups.Items.Count > 0;
        subjects.Enabled = !state && groups.SelectedItem is GroupDto;
        from.Enabled = to.Enabled = week.Enabled = month.Enabled = semester.Enabled = !state && groups.SelectedItem is GroupDto;
        csv.Enabled = excel.Enabled = !state && groups.SelectedItem is GroupDto;
        grid.Enabled = !state && journal is { Columns.Count: > 0 };
        card.Caption = state ? "Загрузка..." : "Журнал посещаемости";
    }

    private readonly record struct JournalFilter(int GroupId, DateOnly From, DateOnly To, string? Subject);
}
