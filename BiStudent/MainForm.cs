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
    private readonly TeacherSession? teacher;
    private readonly CloudFirestore? cloud;
    private readonly CloudWorkspace? workspace;
    private readonly SavePreferences savePreferences = SavePreferences.Load();
    private readonly System.Windows.Forms.Timer autoSaveTimer = new();
    private readonly ModernButton saveChanges = new() { Text = "Сохранить изменения", Width = 210, Enabled = false };
    private bool savingBeforeClose;
    private bool closingWork;
    private string? attendanceError;
    private readonly PublicStatsPublisher? statsPublisher;
    private bool statsDirty = true;
    private bool publishingStats;
    private AttendanceSync? attendanceSync;
    private readonly CameraLoader cameraLoader = new() { Visible = false };
    private CloudSnapshot? cloudSnapshot;
    private Label? pageSubtitle;
    private bool cloudLoading;
    public bool SignedOut { get; private set; }
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

    public MainForm(AttendanceStore? store = null, TeacherSession? teacher = null, CloudFirestore? cloud = null, CloudWorkspace? workspace = null)
    {
        this.store = store ?? new AttendanceStore();
        this.teacher = teacher;
        this.cloud = cloud;
        statsPublisher = cloud is null ? null : new PublicStatsPublisher(cloud, this.store);
        this.workspace = workspace ?? (cloud is null ? null : new CloudWorkspace(cloud, teacher?.Uid ?? "test"));
        cloudSnapshot = this.workspace?.Snapshot;
        if (cloudSnapshot is not null) this.store.ApplyCloudRoster(cloudSnapshot, FirebaseSettings.Load().GroupId);
        if (cloud is not null && cloudSnapshot is not null) attendanceSync = new AttendanceSync(this.store, cloud, cloudSnapshot);
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
        if (this.workspace is not null)
        {
            this.workspace.Changed += WorkspaceChanged;
            WorkspaceChanged();
            saveChanges.Click += async (_, _) => await SaveAndPublishAsync();
            autoSaveTimer.Interval = savePreferences.Seconds * 1000;
            autoSaveTimer.Tick += async (_, _) => await SaveAndPublishAsync(); autoSaveTimer.Start();
            if (cloudSnapshot is null) Shown += async (_, _) => await RefreshCloudAsync();
        }
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

    internal static DataGridView Grid()
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
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var sidebar = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Sidebar, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty, Padding = new Padding(18, 28, 18, 18) };
        sidebar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));
        sidebar.Controls.Add(Label("BiStudent", 17, Theme.Text, FontStyle.Bold), 0, 0);
        var nav = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Sidebar, Margin = Padding.Empty };
        studentsNav.Margin = new Padding(0, 0, 0, 10);
        cameraNav.Margin = Padding.Empty;
        studentsNav.Click += async (_, _) => { await StopCameraAsync(); ShowStudents(); };
        cameraNav.Click += (_, _) => ShowCamera();
        nav.Controls.Add(studentsNav); nav.Controls.Add(cameraNav);
        sidebar.Controls.Add(nav, 0, 1);
        var account = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.Sidebar, Margin = Padding.Empty };
        account.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        account.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); account.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        var accountHeading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        accountHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); accountHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
        accountHeading.Controls.Add(Label(teacher?.Name ?? "Режим проверки", 10, Theme.Text, FontStyle.Bold), 0, 0);
        var settingsButton = new ModernButton { Text = "⚙", Size = new System.Drawing.Size(36, 36), Anchor = AnchorStyles.None, Margin = Padding.Empty, Enabled = workspace is not null };
        settingsButton.Click += (_, _) => { using var settings = new AutoSaveSettingsForm(savePreferences.Seconds); if (settings.ShowDialog(this) == DialogResult.OK) { savePreferences.Seconds = settings.Seconds; savePreferences.Save(); autoSaveTimer.Interval = settings.Seconds * 1000; } };
        accountHeading.Controls.Add(settingsButton, 1, 0); account.Controls.Add(accountHeading, 0, 0);
        var signOut = new ModernButton { Text = "Выйти", Dock = DockStyle.Fill, Margin = Padding.Empty, Enabled = teacher is not null };
        signOut.Click += async (_, _) => { signOut.Enabled = false; await StopCameraAsync(); SignedOut = true; Close(); };
        account.Controls.Add(signOut, 0, 1); sidebar.Controls.Add(account, 0, 2);
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
        pageSubtitle = Label(subtitle, 9, Theme.Muted); heading.Controls.Add(pageSubtitle, 0, 1);
        page.Controls.Add(heading, 0, 0);
        content.Controls.Add(page);
        return page;
    }

    private static FlowLayoutPanel Actions() => new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Background };

    private void ShowStudents()
    {
        cameraPage = false;
        studentsNav.Selected = true; studentsNav.Invalidate(); cameraNav.Selected = false; cameraNav.Invalidate();
        var page = Page("Студенты", cloud is null ? "Регистрация и список студентов" : cloudSnapshot is null ? "Загрузка группы из Firebase…" : cloudSnapshot.GroupName + " · Студенты и оценки в Firebase");
        var actions = Actions();
        var add = new ModernButton { Text = cloud is null ? "+ Добавить студента" : "Обновить из Firebase", Primary = true, Width = 192 };
        add.Click += async (_, _) =>
        {
            if (cloud is not null) { add.Enabled = false; await RefreshCloudAsync(); if (!add.IsDisposed) add.Enabled = true; return; }
            using var form = new EnrollmentForm(store); if (form.ShowDialog(this) == DialogResult.OK) { trainingRevision++; RefreshStudents(); }
        };
        var updateFace = new ModernButton { Text = "Обновить лицо", Width = 155 };
        updateFace.Click += (_, _) =>
        {
            if (studentsTable.CurrentRow?.Cells["Id"].Value is not int id) return;
            var student = VisibleStudents().FirstOrDefault(s => s.Id == id);
            if (student is null) return;
            using var form = new EnrollmentForm(store, student);
            if (form.ShowDialog(this) == DialogResult.OK) { trainingRevision++; RefreshStudents(); }
        };
        var delete = new ModernButton { Text = "Удалить", Width = 120 };
        delete.Click += (_, _) =>
        {
            if (studentsTable.CurrentRow?.Cells["Id"].Value is not int id) return;
            if (MessageBox.Show(this, "Удалить студента, его отметки и оценки?", "Подтверждение", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            store.DeleteStudent(id); trainingRevision++; RefreshStudents();
        };
        var grades = new ModernButton { Text = "Оценки", Width = 125, Enabled = teacher is not null };
        grades.Click += (_, _) =>
        {
            if (teacher is null || studentsTable.CurrentRow?.Cells["Id"].Value is not int id) return;
            var student = VisibleStudents().FirstOrDefault(s => s.Id == id);
            if (student is null) return;
            if (cloud is not null && cloudSnapshot is not null)
            {
                using var form = new CloudGradesForm(workspace!, student.CloudId.Split('/').Last(), student.Name, cloudSnapshot.Subjects);
                form.ShowDialog(this); RefreshStudents();
            }
            else { using var form = new GradesForm(store, student, teacher); form.ShowDialog(this); RefreshStudents(); }
        };
        actions.Controls.Add(add); actions.Controls.Add(updateFace); actions.Controls.Add(grades); if (cloud is null) actions.Controls.Add(delete); else actions.Controls.Add(saveChanges); page.Controls.Add(actions, 0, 1);
        var card = new SmoothPanel { Dock = DockStyle.Fill, Padding = new Padding(6) }; card.Controls.Add(studentsTable); page.Controls.Add(card, 0, 2);
        RefreshStudents();
    }

    private void RefreshStudents()
    {
        var averages = store.GradeAverages();
        if (cloudSnapshot is not null)
            averages = VisibleStudents().Select(s => new { s.Id, Values = cloudSnapshot.Grades.Where(g => g.StudentId == s.CloudId.Split('/').Last() && g.Score is not null).Select(g => g.Score!.Value).ToList() }).Where(x => x.Values.Count > 0).ToDictionary(x => x.Id, x => x.Values.Average());
        studentsTable.DataSource = VisibleStudents().Select(s => new { s.Id, Студент = s.Name, Группа = s.GroupName, Образцов = s.SampleCount, СреднийБалл = averages.TryGetValue(s.Id, out var average) ? average.ToString("0.0") : "—" }).ToList();
        if (studentsTable.Columns["Id"] is { } column) column.Visible = false;
        if (studentsTable.Columns["СреднийБалл"] is { } averageColumn) averageColumn.HeaderText = "Средний балл";
    }

    private List<Student> VisibleStudents()
    {
        var students = store.Students();
        if (cloud is null) return students;
        if (cloudSnapshot is null) return [];
        var order = cloudSnapshot.Students.ToDictionary(x => x.Id, x => x.Ordinal);
        return students.Where(s => s.GroupName == cloudSnapshot.GroupName && order.ContainsKey(s.CloudId.Split('/').Last())).OrderBy(s => order[s.CloudId.Split('/').Last()]).ToList();
    }

    private async Task RefreshCloudAsync()
    {
        if (workspace is null || cloudLoading) return;
        cloudLoading = true;
        if (pageSubtitle is not null) { pageSubtitle.Text = "Загрузка группы из Firebase…"; pageSubtitle.ForeColor = Theme.Muted; }
        try
        {
            await workspace.LoadAsync();
            var snapshot = workspace.Snapshot!;
            if (IsDisposed) return;
            store.ApplyCloudRoster(snapshot, FirebaseSettings.Load().GroupId);
            attendanceSync = new AttendanceSync(store, cloud!, snapshot);
            cloudSnapshot = snapshot; trainingRevision++;
            if (!cameraPage) ShowStudents(); else RefreshSchedule();
        }
        catch (Exception ex) { if (!IsDisposed && pageSubtitle is not null) { pageSubtitle.ForeColor = Theme.Red; pageSubtitle.Text = ex is InvalidOperationException ? ex.Message : "Не удалось загрузить Firebase. Проверьте интернет и нажмите Обновить."; } }
        finally { cloudLoading = false; }
    }

    private void WorkspaceChanged()
    {
        if (IsDisposed || workspace is null) return;
        cloudSnapshot = workspace.Snapshot;
        if (workspace.HasChanges) statsDirty = true;
        saveChanges.Enabled = (workspace.HasChanges || attendanceSync?.HasChanges == true) && !workspace.IsSaving && !publishingStats;
        if (!cameraPage) RefreshStudents();
        if (pageSubtitle is not null) { pageSubtitle.ForeColor = workspace.Error is null && attendanceError is null ? Theme.Muted : Theme.Red; pageSubtitle.Text = workspace.Error ?? attendanceError ?? (workspace.IsSaving ? "Сохранение изменений в Firebase…" : workspace.HasChanges || attendanceSync?.HasChanges == true ? $"Есть несохранённые изменения · Автосохранение каждые {savePreferences.Seconds} сек." : (cloudSnapshot?.GroupName ?? "") + " · Все изменения сохранены"); }
    }

    private async Task SaveAndPublishAsync()
    {
        if (workspace is null || publishingStats) return;
        if (!workspace.HasChanges && attendanceSync?.HasChanges != true && !statsDirty) return;
        publishingStats = true;
        attendanceError = null;
        try {
        await workspace.SaveAsync();
        if (attendanceSync is not null) await attendanceSync.SaveAsync();
        if (workspace.IsSaving || workspace.HasChanges || workspace.Snapshot is null || !statsDirty || statsPublisher is null) return;
        statsDirty = false;
        await statsPublisher.PublishAsync(workspace.Snapshot);
        }
        catch { statsDirty = true; attendanceError = "Не удалось сохранить посещаемость или обновить сайт. Повторим автоматически; данные остаются на компьютере."; }
        finally {
            publishingStats = false;
            saveChanges.Enabled = (workspace.HasChanges || attendanceSync?.HasChanges == true) && !workspace.IsSaving;
            if (pageSubtitle is not null && attendanceError is not null) { pageSubtitle.ForeColor = Theme.Red; pageSubtitle.Text = attendanceError; }
        }
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
        addLesson.Click += (_, _) => { using var form = new LessonForm(store, cloudSnapshot); if (form.ShowDialog(this) == DialogResult.OK) RefreshSchedule(); };
        var removeLesson = new ModernButton { Text = "Удалить занятие", Width = 160 };
        removeLesson.Click += (_, _) =>
        {
            if (scheduleTable.CurrentRow?.Cells["Id"].Value is not int id) return;
            if (MessageBox.Show(this, "Удалить занятие и его отметки?", "Подтверждение", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            store.DeleteLesson(id); statsDirty = true; WorkspaceChanged(); RefreshSchedule();
        };
        actions.Controls.Add(start); actions.Controls.Add(stop); actions.Controls.Add(addLesson); actions.Controls.Add(removeLesson);
        page.Controls.Add(actions, 0, 1);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Background };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 61)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 39));
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, Margin = new Padding(0, 0, 12, 0) };
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); left.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); left.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        left.Controls.Add(lessonLabel, 0, 0);
        var videoCard = new SmoothPanel { Dock = DockStyle.Fill, Padding = new Padding(6) }; videoCard.Controls.Add(preview); preview.Controls.Add(cameraLoader); cameraLoader.BringToFront(); left.Controls.Add(videoCard, 0, 1);
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
        scheduleTable.DataSource = store.Lessons().Where(l => cloud is null || l.GroupName == cloudSnapshot?.GroupName).Select(l => new { l.Id, День = l.Day switch { DayOfWeek.Monday => "Пн", DayOfWeek.Tuesday => "Вт", DayOfWeek.Wednesday => "Ср", DayOfWeek.Thursday => "Чт", DayOfWeek.Friday => "Пт", DayOfWeek.Saturday => "Сб", _ => "Вс" }, Предмет = l.Subject + (l.Room.Length > 0 ? " · ауд. " + l.Room : ""), Группа = l.GroupName, Время = $"{l.Start:hh\\:mm}–{l.End:hh\\:mm}", Неделя = l.Parity switch { 1 => "Нечётная", 2 => "Чётная", _ => "Каждая" } }).ToList();
        if (scheduleTable.Columns["Id"] is { } column) column.Visible = false;
        RefreshLesson();
    }

    private void RefreshLesson()
    {
        var now = DateTime.Now;
        var current = store.LessonsOn(DateOnly.FromDateTime(now)).Where(l => (cloud is null || l.GroupName == cloudSnapshot?.GroupName) && now.TimeOfDay >= l.Start && now.TimeOfDay < l.End).ToList();
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
        statsDirty = true;
        WorkspaceChanged();
        RefreshAttendance();
    }

    private Task StartCameraAsync()
    {
        if (cameraTask is { IsCompleted: false }) return Task.CompletedTask;
        cameraCancellation = new CancellationTokenSource();
        cameraTask = Task.Run(() => CameraLoop(cameraCancellation.Token));
        status.Text = "Подключение к камере…";
        cameraLoader.Text = "Подключение камеры…"; cameraLoader.Visible = true; cameraLoader.BringToFront();
        return Task.CompletedTask;
    }

    private async Task StopCameraAsync()
    {
        cameraCancellation?.Cancel();
        if (cameraTask != null) { try { await cameraTask; } catch (OperationCanceledException) { } cameraTask = null; }
        cameraCancellation?.Dispose(); cameraCancellation = null;
        var old = preview.Image; preview.Image = null; old?.Dispose();
        status.Text = "Камера остановлена";
        cameraLoader.Visible = false;
    }

    private void CameraLoop(CancellationToken token)
    {
        try
        {
            using var capture = CameraDevice.Open(token);
            using (var first = new Mat()) {
                if (capture.Read(first) && !first.Empty()) {
                    var image = BitmapConverter.ToBitmap(first);
                    SafeUi(() => { var old = preview.Image; preview.Image = image; old?.Dispose(); cameraLoader.Visible = false; status.Text = "Камера подключена · Подготовка распознавания…"; }, image.Dispose);
                }
            }
            var allowed = VisibleStudents().Select(s => s.Id).ToHashSet();
            var samples = store.Samples().Where(x => allowed.Contains(x.StudentId)).ToList();
            var preparing = Task.Run(() => {
                var faceEngine = new FaceEngine();
                try { faceEngine.Train(samples, token); return faceEngine; }
                catch { faceEngine.Dispose(); throw; }
            });
            using (var raw = new Mat()) {
                while (!preparing.IsCompleted) {
                    if (!token.IsCancellationRequested && capture.Read(raw) && !raw.Empty() && Interlocked.Exchange(ref pendingFrame, 1) == 0) {
                        var image = BitmapConverter.ToBitmap(raw);
                        SafeUi(() => { if (!token.IsCancellationRequested) { var old = preview.Image; preview.Image = image; old?.Dispose(); } else image.Dispose(); Interlocked.Exchange(ref pendingFrame, 0); }, () => { image.Dispose(); Interlocked.Exchange(ref pendingFrame, 0); });
                    }
                    Thread.Sleep(30);
                }
            }
            using var engine = preparing.GetAwaiter().GetResult();
            token.ThrowIfCancellationRequested();
            SafeUi(() => { cameraLoader.Visible = false; status.Text = "Камера работает · Распознавание готово"; });
            var revision = trainingRevision;
            var streak = new Dictionary<int, int>();
            int? lastLessonId = activeLesson?.Id;
            var students = VisibleStudents();
            var names = students.ToDictionary(s => s.Id, s => s.Name);
            using var frame = new Mat();
            while (!token.IsCancellationRequested)
            {
                if (!capture.Read(frame) || frame.Empty()) { Thread.Sleep(80); continue; }
                if (revision != trainingRevision)
                {
                    allowed = VisibleStudents().Select(s => s.Id).ToHashSet();
                    engine.Train(store.Samples().Where(x => allowed.Contains(x.StudentId)), token); revision = trainingRevision; streak.Clear();
                    students = VisibleStudents(); names = students.ToDictionary(s => s.Id, s => s.Name);
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
                        if (store.MarkPresent(lesson.Id, DateOnly.FromDateTime(DateTime.Today), id, "Камера")) SafeUi(() => { statsDirty = true; WorkspaceChanged(); RefreshAttendance(); });
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
        catch (OperationCanceledException) { }
        catch (Exception ex) { SafeUi(() => { status.Text = ex.Message; cameraLoader.Visible = false; }); }
    }

    private void SafeUi(Action action, Action? failure = null)
    {
        try { if (IsHandleCreated && !IsDisposed) BeginInvoke(action); else failure?.Invoke(); }
        catch { failure?.Invoke(); }
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (closingWork) { e.Cancel = true; return; }
        if (!savingBeforeClose && workspace is not null && (workspace.HasChanges || workspace.IsSaving || attendanceSync?.HasChanges == true || statsDirty || publishingStats))
        {
            e.Cancel = true; base.OnFormClosing(e);
            closingWork = true;
            await StopCameraAsync();
            while (workspace.IsSaving || publishingStats) await Task.Delay(100);
            await SaveAndPublishAsync();
            if (workspace.HasChanges || attendanceSync?.HasChanges == true) MessageBox.Show(this, "Не удалось сохранить в Firebase. Оценки и посещаемость сохранены на компьютере; отправка повторится после следующего входа.", "Сохранение");
            savingBeforeClose = true; closingWork = false; Close(); return;
        }
        autoSaveTimer.Stop(); autoSaveTimer.Dispose();
        if (workspace is not null) workspace.Changed -= WorkspaceChanged;
        cameraCancellation?.Cancel(); scheduleTimer.Stop();
        base.OnFormClosing(e);
    }
}
