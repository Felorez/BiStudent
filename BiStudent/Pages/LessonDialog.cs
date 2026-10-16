using BiStudent.Contracts;

namespace BiStudent;

internal sealed class LessonDialog : DarkForm
{
    private readonly ApiClient api;
    private readonly LessonDto lesson;
    private readonly AttendanceList list = new(wideButtons: true) { Dock = DockStyle.Fill, EmptyText = "В группе нет студентов" };
    private readonly KpiTile present = new("Присутствуют", 20);
    private readonly KpiTile absent = new("Отсутствуют", 20);
    private readonly KpiTile camera = new("Отмечено камерой", 20) { Accent = true };
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 3000 };
    private bool loading;
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly List<ModernButton> writeButtons = [];

    public LessonDialog(ApiClient api, LessonDto lesson)
    {
        this.api = api;
        this.lesson = lesson;
        Text = $"{lesson.Slot.Subject} · {UiFormat.Date(lesson.Date)}";
        ClientSize = new Size(960, 700);
        MinimumSize = new Size(820, 560);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        Padding = new Padding(26, 18, 26, 20);
        Build();
        list.StatusChosen += MarkAsync;
        list.GradeChosen += GradeAsync;
        refresh.Tick += async (_, _) => { if (writes.CurrentCount > 0) await LoadAsync(); };
        Shown += async (_, _) => { await LoadAsync(); if (!IsDisposed) refresh.Start(); };
    }

    private void Build()
    {
        var head = new TableLayoutPanel { Dock = DockStyle.Top, Height = 74, ColumnCount = 2, RowCount = 2, BackColor = Theme.Background };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        head.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); head.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        head.Controls.Add(Ui.Label(lesson.Slot.Subject, 19, Theme.Text, FontStyle.Bold), 0, 0);
        var room = lesson.Slot.Room is { } r ? $" · ауд. {r}" : "";
        head.Controls.Add(Ui.Label($"{UiFormat.LongDate(lesson.Date)} · {UiFormat.Range(lesson.Slot)} · {lesson.Slot.GroupName}{room}{(lesson.Cancelled ? " · отменена" : "")}", 10, lesson.Cancelled ? Theme.Red : Theme.Muted), 0, 1);
        var actions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Background, Margin = new Padding(0, 8, 0, 0) };
        var all = new ModernButton { Text = "Все присутствуют", Width = 176 };
        all.Click += async (_, _) => await RunAsync(async () => { var r = await api.AllPresentAsync(lesson.Slot.Id, lesson.Date); Ui.Toast(this, $"Отмечено присутствующих: {r.Count}"); });
        var finish = new ModernButton { Text = "Завершить занятие", Primary = true, Width = 190, Margin = Padding.Empty };
        finish.Click += async (_, _) =>
        {
            if (!Ui.Confirm(this, "Завершить занятие?", "Всем студентам без отметки будет поставлено «Н».", "Завершить")) return;
            await RunAsync(async () => { var r = await api.FinishAsync(lesson.Slot.Id, lesson.Date); Ui.Toast(this, r.Count == 0 ? "Все уже были отмечены" : $"Отмечено отсутствующих: {r.Count}"); });
        };
        actions.Controls.Add(all); actions.Controls.Add(finish);
        writeButtons.AddRange([all, finish]);
        head.Controls.Add(actions, 1, 0); head.SetRowSpan(actions, 2);

        var kpis = new TableLayoutPanel { Dock = DockStyle.Top, Height = 112, ColumnCount = 3, BackColor = Theme.Background, Padding = new Padding(0, 10, 0, 12) };
        for (var i = 0; i < 3; i++) kpis.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        foreach (var tile in new[] { present, absent, camera }) { tile.Dock = DockStyle.Fill; kpis.Controls.Add(tile); }
        camera.Margin = Padding.Empty;

        var card = new CardPanel("Студенты группы", "Клик по статусу — отметить, повторный клик — снять; балл — по 100-балльной шкале") { Dock = DockStyle.Fill };
        card.Body.Controls.Add(list);
        Controls.Add(card); Controls.Add(kpis); Controls.Add(head);
    }

    private async Task LoadAsync()
    {
        if (loading || IsDisposed) return;
        loading = true;
        try
        {
            var rows = await api.AttendanceAsync(lesson.Slot.Id, lesson.Date);
            if (IsDisposed) return;
            list.SetRows(rows);
            present.Set(rows.Count(r => r.Status == AttendanceStatus.Present).ToString(), $"из {rows.Count}", Theme.Green);
            absent.Set(rows.Count(r => r.Status is AttendanceStatus.Absent or AttendanceStatus.Excused).ToString(), $"из них по уважительной — {rows.Count(r => r.Status == AttendanceStatus.Excused)}", Theme.Red);
            camera.Set(rows.Count(r => r.Method == MarkMethod.Camera).ToString());
        }
        catch (ApiException ex) { if (!IsDisposed) Ui.Toast(this, ex.Message, true); }
        finally { loading = false; }
    }

    private async Task MarkAsync(AttendanceRowDto row, AttendanceStatus? status)
    {
        if (status != AttendanceStatus.Excused)
        {
            await RunAsync(() => api.MarkAsync(lesson.Slot.Id, lesson.Date, row.StudentId, status));
            return;
        }
        using var dialog = new ExcuseDialog(row.FullName, row.Reason, row.HasDocument, row.Comment);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var (reason, document, comment) = dialog.Result;
        await RunAsync(() => api.MarkAsync(lesson.Slot.Id, lesson.Date, row.StudentId, status, reason, comment, document));
    }

    private async Task GradeAsync(AttendanceRowDto row)
    {
        var column = new JournalColumnDto(JournalColumnKind.Lesson, lesson.Slot.Id, lesson.Date,
            lesson.Slot.Start, lesson.Slot.Subject);
        var cell = new JournalCellDto(row.Status, row.Reason, row.Comment, row.Score);
        using var editor = new GradeEditor(api, column, cell, row.StudentId, row.FullName, scoreOnly: true, attendance: row);
        var changed = false;
        editor.Saved += () => changed = true;
        editor.ShowDialog(this, Cursor.Position);
        if (changed) await LoadAsync();
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (writes.CurrentCount == 0) return;
        await writes.WaitAsync();
        if (IsDisposed) { writes.Release(); return; }
        UseWaitCursor = true;
        list.Enabled = false;
        foreach (var button in writeButtons) button.Enabled = false;
        try
        {
            await action();
            while (loading) await Task.Delay(50);
            await LoadAsync();
        }
        catch (ApiException ex) { if (!IsDisposed) Ui.Toast(this, ex.Message, true); }
        finally
        {
            writes.Release();
            if (!IsDisposed) { UseWaitCursor = false; list.Enabled = true; foreach (var button in writeButtons) button.Enabled = true; }
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        refresh.Stop();
        refresh.Dispose();
        base.OnFormClosed(e);
    }
}
