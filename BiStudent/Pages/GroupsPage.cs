using System.Text;
using BiStudent.Contracts;

namespace BiStudent;

internal sealed class GroupsPage : Page
{
    private readonly FlowLayoutPanel groupCards = new() { Dock = DockStyle.Top, Height = 144, AutoScroll = true, WrapContents = false, BackColor = Theme.Background };
    private readonly CardPanel studentsCard = new("Студенты", "правый клик по строке — действия");
    private readonly DataGridView grid = Ui.Grid();
    private readonly Label status = Ui.Label("", 9, Theme.Muted);
    private readonly Label recognitionNote = Ui.Label("", 9, Theme.Red);
    private readonly ModernButton deleteGroup = new() { Text = "Удалить пустую группу", Danger = true, Width = 220, Visible = false };
    private readonly ContextMenuStrip menu = new() { BackColor = Theme.SurfaceHover, ForeColor = Theme.Text, Font = Theme.Font(10) };
    private readonly ToolStripMenuItem enrollItem = new("Зарегистрировать лицо");
    private readonly ToolStripMenuItem editItem = new("Изменить…");
    private readonly ToolStripMenuItem deleteItem = new("Удалить…");
    private List<GroupDto> groups = [];
    private List<StudentDto> students = [];
    private int? selectedId;
    private bool recognitionAvailable;
    private string recognitionMessage = "";

    public GroupsPage(ApiClient api) : base(api, "Группы и студенты", "Списки групп, посещаемость и регистрация лиц для автоотметки")
    {
        var import = new ModernButton { Text = "Импорт CSV", Width = 140 };
        var addGroup = new ModernButton { Text = "+ Группа", Width = 130 };
        var addStudent = new ModernButton { Text = "+ Студент", Width = 140, Primary = true };
        var backups = new ModernButton { Text = "Резервные копии", Width = 170 };
        backups.Click += (_, _) => { using var dialog = new BackupDialog(Api); dialog.ShowDialog(FindForm()); };
        Actions.Controls.Add(backups); Actions.Controls.Add(import); Actions.Controls.Add(addGroup); Actions.Controls.Add(addStudent);
        import.Click += async (_, _) => await ImportAsync();
        addGroup.Click += async (_, _) => await AddGroupAsync();
        addStudent.Click += async (_, _) => await AddStudentAsync();

        var hint = new AutomationHint("список группы загружается из CSV за секунды, а после регистрации лица студент отмечается камерой без участия преподавателя");
        hint.Margin = new Padding(0, 0, 0, 14);
        studentsCard.Dock = DockStyle.Fill;
        studentsCard.Tools.Controls.Add(deleteGroup);
        deleteGroup.Click += async (_, _) => await DeleteGroupAsync();

        grid.AutoGenerateColumns = false;
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "№", FillWeight = 9 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "ФИО", FillWeight = 44 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Лицо", FillWeight = 22 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Посещаемость за 30 дней", FillWeight = 25 });
        grid.CellMouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0)
                grid.CurrentCell = grid.Rows[e.RowIndex].Cells[1];
        };
        grid.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex >= 0 && grid.Rows[e.RowIndex].Tag is StudentDto student) await EditStudentAsync(student);
        };
        menu.Items.AddRange([enrollItem, editItem, deleteItem]);
        menu.Opening += (_, e) =>
        {
            var student = SelectedStudent();
            if (student is null) { e.Cancel = true; return; }
            enrollItem.Text = student.HasFace || student.FaceUnreadable ? "Обновить лицо" : "Зарегистрировать лицо";
            enrollItem.Enabled = recognitionAvailable;
        };
        enrollItem.Click += async (_, _) => { if (SelectedStudent() is { } student) await EnrollAsync(student); };
        editItem.Click += async (_, _) => { if (SelectedStudent() is { } student) await EditStudentAsync(student); };
        deleteItem.Click += async (_, _) => { if (SelectedStudent() is { } student) await DeleteStudentAsync(student); };
        grid.ContextMenuStrip = menu;

        var info = new Panel { Dock = DockStyle.Top, Height = 32, BackColor = Theme.Surface };
        recognitionNote.Dock = DockStyle.Fill;
        info.Controls.Add(recognitionNote);
        status.Dock = DockStyle.Bottom; status.Height = 25;
        studentsCard.Body.Controls.Add(grid); studentsCard.Body.Controls.Add(info); studentsCard.Body.Controls.Add(status);
        Body.Controls.Add(studentsCard); Body.Controls.Add(groupCards); Body.Controls.Add(hint);
    }

    public override Task LoadAsync() => RunAsync(async () =>
    {
        status.Text = "Загрузка групп…";
        try
        {
            var loadedGroups = await Api.GroupsAsync();
            var recognition = await Api.RecognitionStatusAsync();
            if (IsDisposed) return;
            recognitionAvailable = recognition.Ok;
            recognitionMessage = recognition.Message;
            recognitionNote.Text = recognitionAvailable ? "" : $"Регистрация лиц недоступна: {recognitionMessage}";
            await ReloadAsync(loadedGroups);
        }
        finally { if (!IsDisposed) status.Text = ""; }
    });

    private async Task ReloadAsync(List<GroupDto>? loaded = null)
    {
        groups = loaded ?? await Api.GroupsAsync();
        if (IsDisposed) return;
        if (selectedId is null || groups.All(g => g.Id != selectedId))
            selectedId = groups.FirstOrDefault()?.Id;
        RenderGroups();
        await LoadGroupAsync();
    }

    private void RenderGroups()
    {
        groupCards.SuspendLayout();
        foreach (Control control in groupCards.Controls.Cast<Control>().ToArray()) control.Dispose();
        groupCards.Controls.Clear();
        if (groups.Count == 0)
        {
            var empty = Ui.Label("Групп пока нет. Создайте группу или импортируйте список студентов.", 10, Theme.Muted);
            empty.Dock = DockStyle.None; empty.AutoSize = true;
            groupCards.Controls.Add(empty);
        }
        foreach (var group in groups)
        {
            var card = new SmoothPanel
            {
                Width = 230, Height = 118, Padding = new Padding(16, 10, 16, 10),
                Margin = new Padding(0, 0, 12, 12), Cursor = Cursors.Hand,
                BorderColor = group.Id == selectedId ? Theme.Accent : Theme.Line
            };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, BackColor = Color.Transparent };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 14));
            var title = Ui.Label(group.Name, 12, Theme.Text, FontStyle.Bold);
            var count = Ui.Label($"{group.Students} {UiFormat.Plural(group.Students, "студент", "студента", "студентов")}", 9, Theme.Muted);
            var faces = Ui.Label($"лица: {group.WithFace} из {group.Students}", 9, Theme.Muted);
            var progress = new ProgressLine { Dock = DockStyle.Fill, Value = group.Students == 0 ? 0 : 100d * group.WithFace / group.Students };
            layout.Controls.Add(title, 0, 0); layout.Controls.Add(count, 0, 1);
            layout.Controls.Add(faces, 0, 2); layout.Controls.Add(progress, 0, 3);
            card.Controls.Add(layout);
            void Select(object? sender, EventArgs e) => _ = RunAsync(async () =>
            {
                if (selectedId == group.Id) return;
                selectedId = group.Id;
                RenderGroups();
                await LoadGroupAsync();
            });
            card.Click += Select; layout.Click += Select; title.Click += Select;
            count.Click += Select; faces.Click += Select; progress.Click += Select;
            groupCards.Controls.Add(card);
        }
        groupCards.ResumeLayout();
    }

    private async Task LoadGroupAsync()
    {
        grid.Rows.Clear();
        deleteGroup.Visible = false;
        if (selectedId is not { } id)
        {
            students = [];
            studentsCard.Caption = "Студенты";
            return;
        }
        studentsCard.Caption = $"Студенты · {groups.First(g => g.Id == id).Name}";
        status.Text = "Загрузка студентов и посещаемости…";
        try
        {
            students = await Api.StudentsAsync(id);
            var today = DateOnly.FromDateTime(DateTime.Today);
            var journal = await Api.JournalAsync(id, today.AddDays(-29), today, null);
            if (IsDisposed || selectedId != id) return;
            var percentages = journal.Students.ToDictionary(s => s.StudentId, s => s.Percent);
            for (var i = 0; i < students.Count; i++)
            {
                var student = students[i];
                var percent = 0d;
                var hasLessons = journal.Columns.Count > 0 && percentages.TryGetValue(student.Id, out percent);
                var index = grid.Rows.Add(i + 1, student.FullName, student.HasFace ? "✓ зарегистрировано" : student.FaceUnreadable ? "⚠ переснять" : "нет",
                    hasLessons ? UiFormat.Percent(percent) : "—");
                var row = grid.Rows[index];
                row.Tag = student;
                row.Cells[2].Style.ForeColor = student.HasFace ? Theme.Green : student.FaceUnreadable ? Theme.Yellow : Theme.Muted;
                row.Cells[3].Style.ForeColor = hasLessons ? UiFormat.PercentColor(percent) : Theme.Muted;
            }
            deleteGroup.Visible = students.Count == 0;
        }
        finally { if (!IsDisposed) status.Text = ""; }
    }

    private StudentDto? SelectedStudent() =>
        grid.CurrentRow?.Tag as StudentDto;

    private async Task ImportAsync()
    {
        using var dialog = new OpenFileDialog { Title = "Импорт студентов", Filter = "CSV (*.csv)|*.csv|Все файлы (*.*)|*.*" };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        await RunAsync(async () =>
        {
            var csv = await File.ReadAllTextAsync(dialog.FileName, Encoding.UTF8);
            var result = await Api.ImportStudentsAsync(csv);
            await ReloadAsync();
            Ui.Toast(this, $"Добавлено {result.Added}, пропущено дубликатов {result.Skipped}");
            if (result.Errors.Count > 0) Ui.Message(FindForm(), "Ошибки импорта", string.Join(Environment.NewLine, result.Errors));
        });
    }

    private async Task AddGroupAsync()
    {
        var name = Ui.Prompt(FindForm(), "Новая группа", "Название группы");
        if (string.IsNullOrWhiteSpace(name)) return;
        await RunAsync(async () =>
        {
            var group = await Api.CreateGroupAsync(new CreateGroupRequest(name));
            selectedId = group.Id;
            await ReloadAsync();
        }, "Группа создана");
    }

    private async Task AddStudentAsync()
    {
        using var dialog = new StudentDialog(Api, groups, null, selectedId);
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK) await RunAsync(() => ReloadAsync(), "Студент добавлен");
    }

    private async Task EditStudentAsync(StudentDto student)
    {
        using var dialog = new StudentDialog(Api, groups, student, student.GroupId);
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK) await RunAsync(() => ReloadAsync(), "Данные студента обновлены");
    }

    private async Task EnrollAsync(StudentDto student)
    {
        if (!recognitionAvailable) return;
        using var dialog = new EnrollmentDialog(Api, student);
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK) await RunAsync(() => ReloadAsync(), "Лицо зарегистрировано");
    }

    private async Task DeleteStudentAsync(StudentDto student)
    {
        if (!Ui.Confirm(FindForm(), "Удалить студента", $"Удалить {student.FullName}?", "Удалить", danger: true)) return;
        await RunAsync(async () => { await Api.DeleteStudentAsync(student.Id); await ReloadAsync(); }, "Студент удалён");
    }

    private async Task DeleteGroupAsync()
    {
        if (selectedId is not { } id || students.Count != 0) return;
        var name = groups.First(g => g.Id == id).Name;
        if (!Ui.Confirm(FindForm(), "Удалить группу", $"Удалить пустую группу {name}?", "Удалить", danger: true)) return;
        await RunAsync(async () => { await Api.DeleteGroupAsync(id); selectedId = null; await ReloadAsync(); }, "Группа удалена");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) menu.Dispose();
        base.Dispose(disposing);
    }
}
