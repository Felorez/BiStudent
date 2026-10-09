using OpenCvSharp;
using OpenCvSharp.Extensions;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace BiStudent;

internal sealed class MainForm : Form
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
    private readonly AttendanceStore store;
    private readonly Panel content = new() { Dock = DockStyle.Fill, BackColor = Theme.Background };
    private readonly ModernButton studentsNav = new() { Text = "Студенты", Width = 180, Height = 48 };
    private readonly ModernButton cameraNav = new() { Text = "Камера", Width = 180, Height = 48 };
    private readonly PictureBox preview = new() { Dock = DockStyle.Fill, BackColor = Theme.Sidebar, SizeMode = PictureBoxSizeMode.Zoom };
    private readonly Label status = Label("Камера остановлена", 10, Theme.Muted);
    private readonly Label lessonLabel = Label("Нет занятия", 12, Theme.Text, FontStyle.Bold);
    private readonly DataGridView studentsTable = Grid();
    private readonly DataGridView attendanceTable = Grid();
    private readonly DataGridView scheduleTable = Grid();
    private readonly System.Windows.Forms.Timer scheduleTimer = new() { Interval = 30000 };
    private CancellationTokenSource? cameraCancellation;
    private Task? cameraTask;
    private volatile Lesson? activeLesson;
    private int trainingRevision;
    private int pendingFrame;
    private bool cameraPage;

    public MainForm(AttendanceStore? store = null)
    {
        this.store = store ?? new AttendanceStore();
        Text = "BiStudent · Панель преподавателя";
        Size = new System.Drawing.Size(1320, 800);
        MinimumSize = new System.Drawing.Size(1040, 680);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Background;
        Font = Theme.Font(10);
        AutoScaleMode = AutoScaleMode.Dpi;
        BuildShell();
        scheduleTimer.Tick += (_, _) => { if (cameraPage) RefreshLesson(); };
        scheduleTimer.Start();
        ShowStudents();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var enabled = 1;
        if (DwmSetWindowAttribute(Handle, 20, ref enabled, sizeof(int)) != 0)
            DwmSetWindowAttribute(Handle, 19, ref enabled, sizeof(int));
    }

    private static Label Label(string text, float size, Color color, FontStyle style = FontStyle.Regular) => new()
    { Text = text, Font = Theme.Font(size, style), ForeColor = color, BackColor = Color.Transparent, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };

    private static DataGridView Grid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, BackgroundColor = Theme.Surface, BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, GridColor = Theme.Line,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None, RowHeadersVisible = false,
            EnableHeadersVisualStyles = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false, ReadOnly = true, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            ColumnHeadersHeight = 43, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            Font = Theme.Font(10)
        };
        grid.RowTemplate.Height = 43;
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Theme.Surface, ForeColor = Theme.Muted, Font = Theme.Font(9, FontStyle.Bold), SelectionBackColor = Theme.Surface, SelectionForeColor = Theme.Muted, Padding = new Padding(8, 0, 0, 0) };
        grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Theme.Surface, ForeColor = Theme.Text, SelectionBackColor = Theme.SurfaceHover, SelectionForeColor = Theme.Text, Padding = new Padding(8, 0, 0, 0) };
        grid.AlternatingRowsDefaultCellStyle = grid.DefaultCellStyle;
        return grid;
    }

    private void BuildShell()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 216));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var sidebar = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Sidebar, RowCount = 3, Padding = new Padding(18, 28, 18, 18) };
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        sidebar.Controls.Add(Label("CLASSROOM", 17, Theme.Text, FontStyle.Bold), 0, 0);
        var nav = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Sidebar };
        studentsNav.Margin = new Padding(0, 0, 0, 10);
        cameraNav.Margin = Padding.Empty;
        studentsNav.Click += async (_, _) => { await StopCameraAsync(); ShowStudents(); };
        cameraNav.Click += (_, _) => ShowCamera();
        nav.Controls.Add(studentsNav); nav.Controls.Add(cameraNav);
        sidebar.Controls.Add(nav, 0, 1);
        sidebar.Controls.Add(Label("Локальная система\nпосещаемости", 9, Theme.Muted), 0, 2);
        layout.Controls.Add(sidebar, 0, 0);
        layout.Controls.Add(content, 1, 0);
        Controls.Add(layout);
    }

    private TableLayoutPanel Page(string title, string subtitle)
    {
        content.Controls.Clear();
        var page = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(28), BackColor = Theme.Background };
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, BackColor = Theme.Background, Margin = Padding.Empty };
        heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        heading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        heading.Controls.Add(Label(title, 23, Theme.Text, FontStyle.Bold), 0, 0);
        heading.Controls.Add(Label(subtitle, 9, Theme.Muted), 0, 1);
        page.Controls.Add(heading, 0, 0);
        content.Controls.Add(page);
        return page;
    }

    private static FlowLayoutPanel Actions() => new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Background };

    private void ShowStudents()
    {
        cameraPage = false;
        studentsNav.Selected = true; studentsNav.Invalidate(); cameraNav.Selected = false; cameraNav.Invalidate();
        var page = Page("Студенты", "Регистрация и список студентов");
        var actions = Actions();
        var add = new ModernButton { Text = "+ Добавить студента", Primary = true, Width = 192 };
        add.Click += (_, _) => { using var form = new EnrollmentForm(store); if (form.ShowDialog(this) == DialogResult.OK) { trainingRevision++; RefreshStudents(); } };
        var updateFace = new ModernButton { Text = "Обновить лицо", Width = 155 };
        updateFace.Click += (_, _) =>
        {
            if (studentsTable.CurrentRow?.Cells["Id"].Value is not int id) return;
            var student = store.Students().FirstOrDefault(s => s.Id == id);
            if (student is null) return;
            using var form = new EnrollmentForm(store, student);
            if (form.ShowDialog(this) == DialogResult.OK) { trainingRevision++; RefreshStudents(); }
        };
        var delete = new ModernButton { Text = "Удалить", Width = 120 };
        delete.Click += (_, _) =>
        {
            if (studentsTable.CurrentRow?.Cells["Id"].Value is not int id) return;
            if (MessageBox.Show(this, "Удалить студента и его отметки?", "Подтверждение", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            store.DeleteStudent(id); trainingRevision++; RefreshStudents();
        };
        actions.Controls.Add(add); actions.Controls.Add(updateFace); actions.Controls.Add(delete); page.Controls.Add(actions, 0, 1);
        var card = new SmoothPanel { Dock = DockStyle.Fill, Padding = new Padding(6) }; card.Controls.Add(studentsTable); page.Controls.Add(card, 0, 2);
        RefreshStudents();
    }

    private void RefreshStudents()
    {
        studentsTable.DataSource = store.Students().Select(s => new { s.Id, Студент = s.Name, Группа = s.GroupName, Образцов = s.SampleCount }).ToList();
        if (studentsTable.Columns["Id"] is { } column) column.Visible = false;
    }

    private void ShowCamera()
    {
        cameraPage = true;
        studentsNav.Selected = false; studentsNav.Invalidate(); cameraNav.Selected = true; cameraNav.Invalidate();
        var page = Page("Камера", "Видеопоток, текущий урок и посещаемость");
        var actions = Actions();
        var start = new ModernButton { Text = "▶ Запустить", Primary = true, Width = 145 };
        start.Click += async (_, _) => await StartCameraAsync();
        var stop = new ModernButton { Text = "Остановить", Width = 135 };
        stop.Click += async (_, _) => await StopCameraAsync();
        var addLesson = new ModernButton { Text = "+ Занятие", Width = 128 };
        addLesson.Click += (_, _) => { using var form = new LessonForm(store); if (form.ShowDialog(this) == DialogResult.OK) RefreshSchedule(); };
        var removeLesson = new ModernButton { Text = "Удалить занятие", Width = 160 };
        removeLesson.Click += (_, _) =>
        {
            if (scheduleTable.CurrentRow?.Cells["Id"].Value is not int id) return;
            if (MessageBox.Show(this, "Удалить занятие и его отметки?", "Подтверждение", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            store.DeleteLesson(id); RefreshSchedule();
        };
        var export = new ModernButton { Text = "Экспорт CSV", Width = 140 };
        export.Click += (_, _) =>
        {
            var rows = store.Journal();
            if (rows.Count == 0) { MessageBox.Show(this, "Пока нет отметок для экспорта.", "Экспорт"); return; }
            using var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = $"attendance-{DateTime.Today:yyyy-MM-dd}.csv", DefaultExt = "csv", OverwritePrompt = true };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try { JournalExport.Write(dialog.FileName, rows); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageBox.Show(this, ex.Message, "Не удалось сохранить файл"); return; }
            status.Text = $"Журнал сохранён: {Path.GetFileName(dialog.FileName)} · строк: {rows.Count}";
        };
        actions.Controls.Add(start); actions.Controls.Add(stop); actions.Controls.Add(addLesson); actions.Controls.Add(removeLesson); actions.Controls.Add(export);
        page.Controls.Add(actions, 0, 1);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Background };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 61)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 39));
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, Margin = new Padding(0, 0, 12, 0) };
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); left.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); left.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        left.Controls.Add(lessonLabel, 0, 0);
        var videoCard = new SmoothPanel { Dock = DockStyle.Fill, Padding = new Padding(6) }; videoCard.Controls.Add(preview); left.Controls.Add(videoCard, 0, 1);
        left.Controls.Add(status, 0, 2); body.Controls.Add(left, 0, 0);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, Margin = Padding.Empty };
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 39)); right.RowStyles.Add(new RowStyle(SizeType.Percent, 52)); right.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); right.RowStyles.Add(new RowStyle(SizeType.Absolute, 39)); right.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
        right.Controls.Add(Label("Посещаемость", 13, Theme.Text, FontStyle.Bold), 0, 0);
        var attendanceCard = new SmoothPanel { Dock = DockStyle.Fill, Padding = new Padding(4) }; attendanceCard.Controls.Add(attendanceTable); right.Controls.Add(attendanceCard, 0, 1);
        var markActions = Actions();
        var mark = new ModernButton { Text = "Отметить", Width = 128, Height = 37 };
        var unmark = new ModernButton { Text = "Снять отметку", Width = 157, Height = 37 };
        mark.Click += (_, _) => ChangeMark(true); unmark.Click += (_, _) => ChangeMark(false);
        markActions.Controls.Add(mark); markActions.Controls.Add(unmark); right.Controls.Add(markActions, 0, 2);
        right.Controls.Add(Label("Расписание", 13, Theme.Text, FontStyle.Bold), 0, 3);
        var scheduleCard = new SmoothPanel { Dock = DockStyle.Fill, Padding = new Padding(4) }; scheduleCard.Controls.Add(scheduleTable); right.Controls.Add(scheduleCard, 0, 4);
        body.Controls.Add(right, 1, 0); page.Controls.Add(body, 0, 2);
        RefreshSchedule();
    }

    private void RefreshSchedule()
    {
        scheduleTable.DataSource = store.Lessons().Select(l => new { l.Id, День = JournalExport.ShortDay(l.Day), Предмет = l.Subject, Группа = l.GroupName, Время = $"{l.Start:hh\\:mm}–{l.End:hh\\:mm}" }).ToList();
        if (scheduleTable.Columns["Id"] is { } column) column.Visible = false;
        RefreshLesson();
    }

    private void RefreshLesson()
    {
        var now = DateTime.Now;
        var current = store.Lessons().Where(l => l.Day == now.DayOfWeek && now.TimeOfDay >= l.Start && now.TimeOfDay < l.End).ToList();
        activeLesson = current.Count == 1 ? current[0] : null;
        lessonLabel.Text = current.Count > 1 ? "Несколько занятий одновременно — уточните расписание" : activeLesson is null ? "Нет текущего занятия по расписанию" : $"Сейчас: {activeLesson}";
        RefreshAttendance();
    }

    private void RefreshAttendance()
    {
        if (activeLesson is null) { attendanceTable.DataSource = null; return; }
        var rows = store.Attendance(activeLesson.Id, DateOnly.FromDateTime(DateTime.Today), activeLesson.GroupName);
        attendanceTable.DataSource = rows.Select(r => new { r.StudentId, Студент = r.Name, Статус = r.SeenAt is null ? "Нет отметки" : "Присутствует", Время = r.SeenAt?.ToString("HH:mm") ?? "—" }).ToList();
        if (attendanceTable.Columns["StudentId"] is { } column) column.Visible = false;
        status.Text = $"Присутствуют {rows.Count(r => r.SeenAt is not null)} из {rows.Count} · Фото на экране может обмануть распознавание";
    }

    private void ChangeMark(bool present)
    {
        if (activeLesson is null || attendanceTable.CurrentRow?.Cells["StudentId"].Value is not int id) return;
        var date = DateOnly.FromDateTime(DateTime.Today);
        if (present) store.MarkPresent(activeLesson.Id, date, id, "Вручную"); else store.RemoveMark(activeLesson.Id, date, id);
        RefreshAttendance();
    }

    private Task StartCameraAsync()
    {
        if (cameraTask is { IsCompleted: false }) return Task.CompletedTask;
        cameraCancellation = new CancellationTokenSource();
        cameraTask = Task.Run(() => CameraLoop(cameraCancellation.Token));
        status.Text = "Подключение к камере…";
        return Task.CompletedTask;
    }

    private async Task StopCameraAsync()
    {
        cameraCancellation?.Cancel();
        if (cameraTask != null) { try { await cameraTask; } catch (OperationCanceledException) { } cameraTask = null; }
        cameraCancellation?.Dispose(); cameraCancellation = null;
        var old = preview.Image; preview.Image = null; old?.Dispose();
        status.Text = "Камера остановлена";
    }

    private void CameraLoop(CancellationToken token)
    {
        try
        {
            using var capture = new VideoCapture(0);
            if (!capture.IsOpened()) throw new InvalidOperationException("Веб-камера не найдена или занята другой программой.");
            capture.FrameWidth = 960; capture.FrameHeight = 540;
            using var engine = new FaceEngine();
            engine.Train(store.Samples());
            var revision = trainingRevision;
            var streak = new Dictionary<int, int>();
            int? lastLessonId = activeLesson?.Id;
            var students = store.Students();
            var names = students.ToDictionary(s => s.Id, s => s.Name);
            using var frame = new Mat();
            while (!token.IsCancellationRequested)
            {
                if (!capture.Read(frame) || frame.Empty()) { Thread.Sleep(80); continue; }
                if (revision != trainingRevision)
                {
                    engine.Train(store.Samples()); revision = trainingRevision; streak.Clear();
                    students = store.Students(); names = students.ToDictionary(s => s.Id, s => s.Name);
                }
                if (lastLessonId != activeLesson?.Id) { streak.Clear(); lastLessonId = activeLesson?.Id; }
                var matches = engine.Scan(frame);
                var detectedIds = matches.Where(m => m.StudentId.HasValue).Select(m => m.StudentId!.Value).ToHashSet();
                foreach (var id in streak.Keys.ToArray()) if (!detectedIds.Contains(id)) streak.Remove(id);
                foreach (var match in matches)
                {
                    if (match.StudentId is not int id) continue;
                    streak[id] = streak.GetValueOrDefault(id) + 1;
                    var lesson = activeLesson;
                    if (streak[id] >= 5 && lesson != null && students.Any(s => s.Id == id && s.GroupName == lesson.GroupName))
                    {
                        if (store.MarkPresent(lesson.Id, DateOnly.FromDateTime(DateTime.Today), id, "Камера")) SafeUi(RefreshAttendance);
                        streak[id] = 0;
                    }
                }
                if (Interlocked.Exchange(ref pendingFrame, 1) != 0) continue;
                var bitmap = BitmapConverter.ToBitmap(frame);
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    foreach (var match in matches)
                    {
                        var studentId = match.StudentId ?? -1;
                        var known = names.TryGetValue(studentId, out var matchedName);
                        var color = known ? Theme.Green : Theme.Red;
                        using var pen = new Pen(color, 3);
                        g.DrawRectangle(pen, match.Bounds.X, match.Bounds.Y, match.Bounds.Width, match.Bounds.Height);
                        var name = known ? matchedName! : "Неизвестный";
                        using var font = Theme.Font(13, FontStyle.Bold);
                        using var brush = new SolidBrush(color);
                        using var bg = new SolidBrush(Color.FromArgb(210, 11, 18, 32));
                        var y = Math.Max(0, match.Bounds.Y - 32);
                        g.FillRectangle(bg, match.Bounds.X, y, g.MeasureString(name, font).Width + 12, 30);
                        g.DrawString(name, font, brush, match.Bounds.X + 5, y + 2);
                    }
                }
                SafeUi(() => { var old = preview.Image; preview.Image = bitmap; old?.Dispose(); Interlocked.Exchange(ref pendingFrame, 0); }, () => { bitmap.Dispose(); Interlocked.Exchange(ref pendingFrame, 0); });
            }
        }
        catch (Exception ex) { SafeUi(() => status.Text = ex.Message); }
    }

    private void SafeUi(Action action, Action? failure = null)
    {
        try { if (IsHandleCreated && !IsDisposed) BeginInvoke(action); else failure?.Invoke(); }
        catch { failure?.Invoke(); }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        cameraCancellation?.Cancel(); scheduleTimer.Stop();
        base.OnFormClosing(e);
    }
}
