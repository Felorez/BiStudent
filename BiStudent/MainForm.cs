using BiStudent.Contracts;

namespace BiStudent;

internal sealed class MainForm : DarkForm
{
    private static readonly (string Key, string Text, string Icon)[] Pages =
    [
        ("today", "Сегодня", "◷"), ("camera", "Автоотметка", "◉"), ("schedule", "Расписание", "▦"),
        ("journal", "Журнал", "☰"), ("groups", "Группы", "◍"), ("report", "Отчёт для руководства", "▲")
    ];

    private readonly ApiClient api;
    private readonly Panel host = new() { Dock = DockStyle.Fill, BackColor = Theme.Background };
    private readonly Dictionary<string, ModernButton> nav = [];
    private Page? current;
    private Label? userName;
    private Label? userAvatar;
    private bool switching;
    private bool closingAfterLeave;
    private bool leaving;

    public MainForm(ApiClient api)
    {
        this.api = api;
        Text = "BiStudent · Кабинет преподавателя";
        Size = new Size(1440, 900);
        MinimumSize = new Size(1180, 720);
        StartPosition = FormStartPosition.CenterScreen;
        BuildShell();
        api.Unauthorized += OnUnauthorized;
        Shown += async (_, _) => { if (AutoOpenFirstPage && current is null) await ShowPageAsync("today"); };
    }

    public bool SignedOut { get; private set; }
    public bool AutoOpenFirstPage { get; set; } = true;
    public LessonDto? PendingCameraLesson { get; set; }
    internal Page? CurrentPage => current;
    internal string? LastPageError { get; private set; }
    internal ApiClient Api => api;

    private void BuildShell()
    {
        var sidebar = new TableLayoutPanel { Dock = DockStyle.Left, Width = 240, BackColor = Theme.Sidebar, RowCount = 3, Padding = new Padding(16, 22, 16, 16) };
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));

        var brand = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, BackColor = Theme.Sidebar, Margin = Padding.Empty };
        brand.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48)); brand.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        brand.RowStyles.Add(new RowStyle(SizeType.Absolute, 26)); brand.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        var mark = new Label { Text = "✓", Size = new Size(38, 38), BackColor = Theme.Accent, ForeColor = Color.White, Font = Theme.Font(16, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter, Margin = new Padding(0, 4, 0, 0) };
        brand.Controls.Add(mark, 0, 0); brand.SetRowSpan(mark, 2);
        brand.Controls.Add(Ui.Label("BiStudent", 15, Theme.Text, FontStyle.Bold), 1, 0);
        brand.Controls.Add(Ui.Label("Посещаемость без переклички", 8.5f, Theme.Muted), 1, 1);
        sidebar.Controls.Add(brand, 0, 0);

        var menu = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Sidebar };
        foreach (var (key, text, icon) in Pages)
        {
            var button = new ModernButton { Text = text, Icon = icon, Width = 206, Height = 44, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, key == "report" ? 14 : 0, 0, 6), Dashed = key == "report", BackColor = Theme.Sidebar };
            button.Click += async (_, _) => await ShowPageAsync(key);
            nav[key] = button;
            menu.Controls.Add(button);
        }
        sidebar.Controls.Add(menu, 0, 1);

        var user = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, BackColor = Theme.Sidebar, Margin = Padding.Empty };
        user.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44)); user.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); user.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
        user.RowStyles.Add(new RowStyle(SizeType.Percent, 55)); user.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        var name = api.Teacher?.FullName ?? "Преподаватель";
        var avatar = new Label { Text = Initials(name), Size = new Size(36, 36), BackColor = Theme.SurfaceAlt, ForeColor = Theme.Text, Font = Theme.Font(10, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter, Margin = new Padding(0, 12, 0, 0) };
        user.Controls.Add(avatar, 0, 0); user.SetRowSpan(avatar, 2);
        userAvatar = avatar;
        var nameLabel = Ui.Label(name, 9.5f, Theme.Text, FontStyle.Bold); nameLabel.AutoEllipsis = true; nameLabel.TextAlign = ContentAlignment.BottomLeft;
        user.Controls.Add(nameLabel, 1, 0);
        userName = nameLabel;
        var roleLabel = Ui.Label("Преподаватель", 8.5f, Theme.Muted); roleLabel.TextAlign = ContentAlignment.TopLeft;
        user.Controls.Add(roleLabel, 1, 1);
        var exit = new ModernButton { Text = "↩", Width = 38, Height = 38, Font = Theme.Font(12, FontStyle.Bold), BackColor = Theme.Sidebar, Margin = new Padding(0, 12, 0, 0) };
        exit.Click += (_, _) => { SignedOut = true; Close(); };
        new ToolTip().SetToolTip(exit, "Выйти");
        user.Controls.Add(exit, 2, 0); user.SetRowSpan(exit, 2);
        sidebar.Controls.Add(user, 0, 2);

        Controls.Add(host);
        Controls.Add(sidebar);
    }

    private static string Initials(string name) =>
        string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => char.IsLetter(p[0])).Take(2).Select(p => char.ToUpperInvariant(p[0])));

    private Page CreatePage(string key) => key switch
    {
        "today" => new TodayPage(api),
        "schedule" => new SchedulePage(api),
        "journal" => new JournalPage(api),
        "groups" => new GroupsPage(api),
        "camera" => new CameraPage(api),
        "report" => new ReportPage(api),
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, null)
    };

    internal async Task ShowPageAsync(string key)
    {
        if (switching || IsDisposed) return;
        switching = true;
        if (api.Teacher is { } teacher && userName is not null && userAvatar is not null) { userName.Text = teacher.FullName; userAvatar.Text = Initials(teacher.FullName); }
        try
        {
            if (current is not null)
            {
                await current.OnLeaveAsync();
                host.Controls.Remove(current);
                current.Dispose();
                current = null;
            }
            foreach (var (pageKey, button) in nav) button.Selected = pageKey == key;
            var page = CreatePage(key);
            current = page;
            host.Controls.Add(page);
            LastPageError = null;
            try { await page.LoadAsync(); }
            catch (ApiException ex) { LastPageError = ex.Message; Ui.Toast(this, ex.Message, true); }
        }
        finally { switching = false; }
    }

    internal Task OpenCamera(LessonDto lesson)
    {
        PendingCameraLesson = lesson;
        return ShowPageAsync("camera");
    }

    internal Task OpenLessonAsync(LessonDto lesson)
    {
        using var dialog = new LessonDialog(api, lesson);
        dialog.ShowDialog(this);
        return current?.LoadAsync() ?? Task.CompletedTask;
    }

    private void OnUnauthorized()
    {
        if (IsDisposed || SignedOut) return;
        BeginInvoke(() =>
        {
            if (SignedOut) return;
            SignedOut = true;
            Ui.Toast(this, "Сессия истекла — войдите снова", true);
            Close();
        });
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (!closingAfterLeave && current is not null)
        {
            e.Cancel = true;
            if (leaving) return;
            leaving = true;
            try { await current.OnLeaveAsync(); }
            finally
            {
                closingAfterLeave = true;
                Close();
            }
            return;
        }
        api.Unauthorized -= OnUnauthorized;
        base.OnFormClosing(e);
    }
}
