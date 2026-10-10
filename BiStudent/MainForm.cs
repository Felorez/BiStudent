using OpenCvSharp;
using OpenCvSharp.Extensions;
using System.Diagnostics;
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
    private Task? cameraStopTask;
    private ModernButton? startButton;
    private ModernButton? stopButton;
    private ComboBox? cameraSelect;
    private bool hasCameraDevices;
    private volatile Lesson? activeLesson;
    private DateOnly activeDate;
    private int trainingRevision;
    private int scheduleRevision;
    private int cameraFailed;
    private int cameraReconnecting;
    private int cameraRunRevision;
    private int pendingFrame;
    private int navigationRequest;
    private bool cameraPage;
    private bool closingCamera;
    private bool closingAfterStop;

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
        studentsNav.Click += async (_, _) =>
        {
            if (closingCamera) return;
            var request = ++navigationRequest;
            await StopCameraAsync();
            if (closingCamera || IsDisposed || Disposing || request != navigationRequest) return;
            ShowStudents();
        };
        cameraNav.Click += (_, _) => { if (!closingCamera) { navigationRequest++; ShowCamera(); } };
        nav.Controls.Add(studentsNav); nav.Controls.Add(cameraNav);
        sidebar.Controls.Add(nav, 0, 1);
        sidebar.Controls.Add(Label("Локальная система\nпосещаемости", 9, Theme.Muted), 0, 2);
        layout.Controls.Add(sidebar, 0, 0);
        layout.Controls.Add(content, 1, 0);
        Controls.Add(layout);
    }

    private TableLayoutPanel Page(string title, string subtitle)
    {
        preview.Parent = null;
        status.Parent = null;
        lessonLabel.Parent = null;
        studentsTable.Parent = null;
        attendanceTable.Parent = null;
        scheduleTable.Parent = null;
        var oldPages = content.Controls.Cast<Control>().ToArray();
        content.Controls.Clear();
        foreach (var oldPage in oldPages) oldPage.Dispose();
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
        startButton = null; stopButton = null; cameraSelect = null;
        studentsNav.Selected = true; studentsNav.Invalidate(); cameraNav.Selected = false; cameraNav.Invalidate();
        var page = Page("Студенты", "Регистрация и список студентов");
        var actions = Actions();
        var add = new ModernButton { Text = "+ Добавить студента", Primary = true, Width = 192 };
        add.Click += (_, _) => { using var form = new EnrollmentForm(store); if (form.ShowDialog(this) == DialogResult.OK) { Interlocked.Increment(ref trainingRevision); RefreshStudents(); } };
        var updateFace = new ModernButton { Text = "Обновить лицо", Width = 155 };
        updateFace.Click += (_, _) =>
        {
            if (studentsTable.CurrentRow?.Cells["Id"].Value is not int id) return;
            var student = store.Students().FirstOrDefault(s => s.Id == id);
            if (student is null) return;
            using var form = new EnrollmentForm(store, student);
            if (form.ShowDialog(this) == DialogResult.OK) { Interlocked.Increment(ref trainingRevision); RefreshStudents(); }
        };
        var delete = new ModernButton { Text = "Удалить", Width = 120 };
        delete.Click += (_, _) =>
        {
            if (studentsTable.CurrentRow?.Cells["Id"].Value is not int id) return;
            if (MessageBox.Show(this, "Удалить студента и его отметки?", "Подтверждение", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            store.DeleteStudent(id); Interlocked.Increment(ref trainingRevision); RefreshStudents();
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
        startButton = start; stopButton = stop;
        var devices = CameraDevices.List();
        hasCameraDevices = devices.Count > 0;
        var selectedIndex = CameraDevices.ResolveIndex(store, devices);
        var camera = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Margin = new Padding(6, 5, 0, 5),
            BackColor = Theme.SurfaceHover, ForeColor = Theme.Text, FlatStyle = FlatStyle.Flat, Font = Theme.Font(11),
            DrawMode = DrawMode.OwnerDrawFixed
        };
        camera.ItemHeight = camera.Font.Height + 8;
        camera.DrawItem += (_, e) =>
        {
            using var background = new SolidBrush(e.Index >= 0 && (e.State & DrawItemState.Selected) != 0 ? Theme.Accent : Theme.SurfaceHover);
            e.Graphics.FillRectangle(background, e.Bounds);
            if (e.Index < 0) return;
            var bounds = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 16), e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, camera.Items[e.Index]?.ToString() ?? "", camera.Font, bounds,
                camera.Enabled ? Theme.Text : Theme.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
        if (hasCameraDevices) camera.Items.AddRange(devices.ToArray());
        else camera.Items.Add(new CameraDevice(0, "Камера по умолчанию", ""));
        camera.SelectedIndex = hasCameraDevices ? Math.Max(0, devices.FindIndex(d => d.Index == selectedIndex)) : 0;
        camera.SelectedIndexChanged += (_, _) => { if (camera.SelectedItem is CameraDevice device) CameraDevices.Save(store, device); };
        cameraSelect = camera;
        var addLesson = new ModernButton { Text = "+ Занятие", Width = 128 };
        addLesson.Click += (_, _) => { using var form = new LessonForm(store); if (form.ShowDialog(this) == DialogResult.OK) { Interlocked.Increment(ref scheduleRevision); RefreshSchedule(); } };
        var removeLesson = new ModernButton { Text = "Удалить занятие", Width = 160 };
        removeLesson.Click += (_, _) =>
        {
            if (scheduleTable.CurrentRow?.Cells["Id"].Value is not int id) return;
            if (MessageBox.Show(this, "Удалить занятие и его отметки?", "Подтверждение", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            store.DeleteLesson(id); Interlocked.Increment(ref scheduleRevision); RefreshSchedule();
        };
        actions.Controls.Add(start); actions.Controls.Add(stop); actions.Controls.Add(addLesson); actions.Controls.Add(removeLesson);
        UpdateCameraButtons(cameraTask is { IsCompleted: false } || cameraStopTask is { IsCompleted: false });
        page.Controls.Add(actions, 0, 1);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Background };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 61)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 39));
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, Margin = new Padding(0, 0, 12, 0) };
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); left.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); left.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        var cameraHeading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        cameraHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        cameraHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
        cameraHeading.Controls.Add(lessonLabel, 0, 0); cameraHeading.Controls.Add(camera, 1, 0);
        left.Controls.Add(cameraHeading, 0, 0);
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
        scheduleTable.DataSource = store.Lessons().Select(l => new { l.Id, День = l.Day switch { DayOfWeek.Monday => "Пн", DayOfWeek.Tuesday => "Вт", DayOfWeek.Wednesday => "Ср", DayOfWeek.Thursday => "Чт", DayOfWeek.Friday => "Пт", DayOfWeek.Saturday => "Сб", _ => "Вс" }, Предмет = l.Subject, Группа = l.GroupName, Время = $"{l.Start:hh\\:mm}–{l.End:hh\\:mm}" }).ToList();
        if (scheduleTable.Columns["Id"] is { } column) column.Visible = false;
        RefreshLesson();
    }

    private static Lesson? LessonAt(IReadOnlyList<Lesson> lessons, DateTime now)
    {
        Lesson? current = null;
        foreach (var lesson in lessons)
        {
            if (lesson.Day != now.DayOfWeek || now.TimeOfDay < lesson.Start || now.TimeOfDay >= lesson.End) continue;
            if (current is not null) return null;
            current = lesson;
        }
        return current;
    }

    private void RefreshLesson()
    {
        var now = DateTime.Now;
        var current = store.Lessons().Where(l => l.Day == now.DayOfWeek && now.TimeOfDay >= l.Start && now.TimeOfDay < l.End).ToList();
        activeLesson = LessonAt(current, now);
        activeDate = DateOnly.FromDateTime(now);
        lessonLabel.Text = current.Count > 1 ? "Несколько занятий одновременно — уточните расписание" : activeLesson is null ? "Нет текущего занятия по расписанию" : $"Сейчас: {activeLesson}";
        RefreshAttendance();
    }

    private void RefreshAttendance()
    {
        if (activeLesson is null) { attendanceTable.DataSource = null; return; }
        var rows = store.Attendance(activeLesson.Id, activeDate, activeLesson.GroupName);
        attendanceTable.DataSource = rows.Select(r => new { r.StudentId, Студент = r.Name, Статус = r.SeenAt is null ? "Нет отметки" : "Присутствует", Время = r.SeenAt?.ToString("HH:mm") ?? "—" }).ToList();
        if (attendanceTable.Columns["StudentId"] is { } column) column.Visible = false;
        if (Volatile.Read(ref cameraFailed) == 0 && Volatile.Read(ref cameraReconnecting) == 0) status.Text = $"Присутствуют {rows.Count(r => r.SeenAt is not null)} из {rows.Count} · Фото на экране может обмануть распознавание";
    }

    private void ChangeMark(bool present)
    {
        var now = DateTime.Now;
        var date = DateOnly.FromDateTime(now);
        var lesson = LessonAt(store.Lessons(), now);
        if (lesson?.Id != activeLesson?.Id || activeDate != date)
        {
            RefreshLesson();
            status.Text = "Занятие сменилось, список обновлён";
            return;
        }
        if (lesson is null || attendanceTable.CurrentRow?.Cells["StudentId"].Value is not int id) return;
        if (present) store.MarkPresent(lesson.Id, date, id, "Вручную"); else store.RemoveMark(lesson.Id, date, id);
        RefreshAttendance();
    }

    private Task StartCameraAsync()
    {
        if (cameraTask is { IsCompleted: false } || cameraStopTask is { IsCompleted: false }) return Task.CompletedTask;
        cameraCancellation?.Dispose();
        cameraCancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref cameraFailed, 0);
        Interlocked.Exchange(ref cameraReconnecting, 0);
        var token = cameraCancellation.Token;
        var run = ++cameraRunRevision;
        Interlocked.Exchange(ref pendingFrame, 0);
        status.Text = "Подключение к камере…";
        UpdateCameraButtons(true);
        var device = cameraSelect?.SelectedItem as CameraDevice ?? new CameraDevice(0, "Камера по умолчанию", "");
        var named = hasCameraDevices;
        if (named) try { CameraDevices.Save(store, device); } catch { }
        cameraTask = Task.Run(() => CameraLoop(token, run, device, named));
        _ = cameraTask.ContinueWith(_ =>
        {
            if (run == cameraRunRevision && !IsDisposed) UpdateCameraButtons(false);
        }, TaskScheduler.FromCurrentSynchronizationContext());
        return Task.CompletedTask;
    }

    private Task StopCameraAsync()
    {
        if (cameraStopTask is { IsCompleted: false }) return cameraStopTask;
        cameraStopTask = StopCameraCoreAsync();
        return cameraStopTask;
    }

    private async Task StopCameraCoreAsync()
    {
        var wasRunning = cameraTask is { IsCompleted: false };
        cameraCancellation?.Cancel();
        if (cameraTask != null)
        {
            try { await cameraTask; }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Interlocked.Exchange(ref cameraFailed, 1); status.Text = $"Камера остановлена из-за ошибки: {ex.Message}"; }
            cameraTask = null;
        }
        cameraCancellation?.Dispose(); cameraCancellation = null;
        var old = preview.Image; preview.Image = null; old?.Dispose();
        Interlocked.Exchange(ref cameraReconnecting, 0);
        UpdateCameraButtons(false);
        if (wasRunning && Volatile.Read(ref cameraFailed) == 0) status.Text = "Камера остановлена";
    }

    private void UpdateCameraButtons(bool running)
    {
        if (startButton is not null) startButton.Enabled = !running;
        if (stopButton is not null) stopButton.Enabled = running;
        if (cameraSelect is not null) cameraSelect.Enabled = !running && hasCameraDevices;
    }

    private void CameraLoop(CancellationToken token, int run, CameraDevice device, bool named)
    {
        VideoCapture? capture = null;
        try
        {
            var index = device.Index;
            if (named)
            {
                var current = CameraDevices.Find(CameraDevices.List(), device.Id)
                    ?? throw new InvalidOperationException("Выбранная камера не подключена. Подключите её или выберите другую.");
                index = current.Index;
            }
            capture = CameraDevices.Open(index, 960, 540, !named);
            var lastFrame = Stopwatch.StartNew();
            var reconnecting = false;
            using var engine = new FaceEngine();
            engine.Train(store.Samples());
            var revision = Volatile.Read(ref trainingRevision);
            var lessonRevision = Volatile.Read(ref scheduleRevision);
            var lessons = store.Lessons();
            var streak = new Dictionary<int, int>();
            int? lastLessonId = null;
            var students = store.Students();
            var names = students.ToDictionary(s => s.Id, s => s.Name);
            using var frame = new Mat();
            lastFrame.Restart();
            var firstFrame = true;
            while (!token.IsCancellationRequested)
            {
                if (reconnecting && capture is null)
                {
                    var reconnectIndex = device.Index;
                    if (named)
                    {
                        var current = CameraDevices.Find(CameraDevices.List(), device.Id);
                        if (current is null)
                        {
                            if (token.WaitHandle.WaitOne(2000)) break;
                            continue;
                        }
                        reconnectIndex = current.Index;
                    }
                    try { capture = CameraDevices.Open(reconnectIndex, 960, 540, !named); }
                    catch
                    {
                        if (token.WaitHandle.WaitOne(2000)) break;
                        continue;
                    }
                    lastFrame.Restart();
                }
                bool ok;
                try { ok = capture!.Read(frame); }
                catch (Exception ex) when (ex is not OperationCanceledException) { ok = false; }
                if (!ok || frame.Empty())
                {
                    if (lastFrame.Elapsed >= TimeSpan.FromSeconds(3))
                    {
                        if (!reconnecting)
                        {
                            reconnecting = true;
                            Interlocked.Exchange(ref cameraReconnecting, 1);
                            SafeUi(() =>
                            {
                                if (run == cameraRunRevision) status.Text = "Нет кадров с камеры — переподключение…";
                            });
                        }
                        capture?.Dispose(); capture = null;
                        if (token.WaitHandle.WaitOne(2000)) break;
                        continue;
                    }
                    if (token.WaitHandle.WaitOne(80)) break;
                    continue;
                }
                lastFrame.Restart();
                if (firstFrame)
                {
                    firstFrame = false;
                    SafeUi(() =>
                    {
                        if (run != cameraRunRevision) return;
                        if (activeLesson is null) status.Text = "Камера работает · нет текущего занятия";
                        else RefreshAttendance();
                    });
                }
                if (reconnecting)
                {
                    reconnecting = false;
                    Interlocked.Exchange(ref cameraReconnecting, 0);
                    streak.Clear();
                    SafeUi(() =>
                    {
                        if (run != cameraRunRevision) return;
                        if (activeLesson is null) status.Text = "Камера снова работает";
                        else RefreshAttendance();
                    });
                }
                var currentRevision = Volatile.Read(ref trainingRevision);
                if (revision != currentRevision)
                {
                    engine.Train(store.Samples()); revision = currentRevision; streak.Clear();
                    students = store.Students(); names = students.ToDictionary(s => s.Id, s => s.Name);
                }
                var currentLessonRevision = Volatile.Read(ref scheduleRevision);
                if (lessonRevision != currentLessonRevision) { lessons = store.Lessons(); lessonRevision = currentLessonRevision; streak.Clear(); }
                var now = DateTime.Now;
                var lesson = LessonAt(lessons, now);
                if (lastLessonId != lesson?.Id) { streak.Clear(); lastLessonId = lesson?.Id; SafeUi(RefreshLesson); }
                var matches = engine.Scan(frame);
                var detectedIds = matches.Where(m => m.StudentId.HasValue).Select(m => m.StudentId!.Value).ToHashSet();
                foreach (var id in streak.Keys.ToArray()) if (!detectedIds.Contains(id)) streak.Remove(id);
                foreach (var match in matches)
                {
                    if (match.StudentId is not int id) continue;
                    streak[id] = streak.GetValueOrDefault(id) + 1;
                    if (streak[id] >= 5 && lesson != null && students.Any(s => s.Id == id && s.GroupName == lesson.GroupName))
                    {
                        if (store.MarkPresent(lesson.Id, DateOnly.FromDateTime(now), id, "Камера")) SafeUi(RefreshAttendance);
                        streak[id] = 0;
                    }
                }
                if (Interlocked.Exchange(ref pendingFrame, 1) != 0) continue;
                Bitmap? bitmap = null;
                try
                {
                    bitmap = BitmapConverter.ToBitmap(frame);
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
                            var font = Theme.Font(13, FontStyle.Bold);
                            using var brush = new SolidBrush(color);
                            using var bg = new SolidBrush(Color.FromArgb(210, 11, 18, 32));
                            var y = Math.Max(0, match.Bounds.Y - 32);
                            g.FillRectangle(bg, match.Bounds.X, y, g.MeasureString(name, font).Width + 12, 30);
                            g.DrawString(name, font, brush, match.Bounds.X + 5, y + 2);
                        }
                    }
                }
                catch
                {
                    try { bitmap?.Dispose(); }
                    finally { Interlocked.Exchange(ref pendingFrame, 0); }
                    throw;
                }
                var readyBitmap = bitmap!;
                SafeUi(() =>
                {
                    if (run != cameraRunRevision) { readyBitmap.Dispose(); return; }
                    if (cameraTask is not { IsCompleted: false }) { readyBitmap.Dispose(); Interlocked.Exchange(ref pendingFrame, 0); return; }
                    var old = preview.Image; preview.Image = readyBitmap; old?.Dispose(); Interlocked.Exchange(ref pendingFrame, 0);
                }, () => { readyBitmap.Dispose(); if (run == cameraRunRevision) Interlocked.Exchange(ref pendingFrame, 0); });
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref cameraFailed, 1);
            SafeUi(() =>
            {
                if (run != cameraRunRevision) return;
                status.Text = $"Камера остановлена из-за ошибки: {ex.Message}";
            });
        }
        finally { capture?.Dispose(); }
    }

    private void SafeUi(Action action, Action? failure = null)
    {
        try { if (IsHandleCreated && !IsDisposed) BeginInvoke(action); else failure?.Invoke(); }
        catch { failure?.Invoke(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            var image = preview.Image; preview.Image = null; image?.Dispose();
            if (!preview.IsDisposed) preview.Dispose();
            if (!status.IsDisposed) status.Dispose();
            if (!lessonLabel.IsDisposed) lessonLabel.Dispose();
            if (!studentsTable.IsDisposed) studentsTable.Dispose();
            if (!attendanceTable.IsDisposed) attendanceTable.Dispose();
            if (!scheduleTable.IsDisposed) scheduleTable.Dispose();
            scheduleTimer.Dispose();
            cameraCancellation?.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        scheduleTimer.Stop();
        if (!closingAfterStop && (cameraTask is { IsCompleted: false } || cameraStopTask is { IsCompleted: false } || closingCamera))
        {
            e.Cancel = true;
            if (closingCamera) return;
            closingCamera = true;
            await StopCameraAsync();
            closingAfterStop = true;
            Close();
            return;
        }
        base.OnFormClosing(e);
    }
}
