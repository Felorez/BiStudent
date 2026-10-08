using OpenCvSharp;
using OpenCvSharp.Extensions;
using System.Globalization;

namespace BiStudent;

internal static class FormStyle
{
    public static TextBox Input(string placeholder) => new()
    {
        PlaceholderText = placeholder, BorderStyle = BorderStyle.FixedSingle, BackColor = Theme.SurfaceHover,
        ForeColor = Theme.Text, Font = Theme.Font(11), Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 8)
    };

    public static Label Caption(string text) => new()
    {
        Text = text, ForeColor = Theme.Muted, Font = Theme.Font(9, FontStyle.Bold),
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft
    };

    public static void Dialog(Form form, string title, System.Drawing.Size size)
    {
        form.Text = title;
        form.Size = size;
        form.MinimumSize = size;
        form.StartPosition = FormStartPosition.CenterParent;
        form.FormBorderStyle = FormBorderStyle.FixedDialog;
        form.MaximizeBox = false;
        form.MinimizeBox = false;
        form.BackColor = Theme.Background;
        form.ForeColor = Theme.Text;
        form.Font = Theme.Font(10);
    }
}

internal sealed class EnrollmentForm : Form
{
    private readonly AttendanceStore store;
    private readonly Student? existing;
    private readonly TextBox nameBox = FormStyle.Input("Фамилия и имя");
    private readonly TextBox groupBox = FormStyle.Input("Например, ИС-21");
    private readonly PictureBox preview = new() { Dock = DockStyle.Fill, BackColor = Theme.Sidebar, SizeMode = PictureBoxSizeMode.Zoom };
    private readonly Label feedback = FormStyle.Caption("Сделайте три снимка: прямо, немного влево и немного вправо.");
    private readonly List<byte[]> samples = new();
    private VideoCapture? capture;
    private FaceEngine? engine;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 110 };
    private Mat? latestFrame;
    private OpenCvSharp.Rect? latestFace;

    public EnrollmentForm(AttendanceStore store, Student? existing = null)
    {
        this.store = store;
        this.existing = existing;
        FormStyle.Dialog(this, existing is null ? "Регистрация студента" : "Обновление лица", new System.Drawing.Size(780, 710));
        if (existing is not null)
        {
            nameBox.Text = existing.Name; groupBox.Text = existing.GroupName;
            nameBox.ReadOnly = true; groupBox.ReadOnly = true;
        }
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), RowCount = 7, BackColor = Theme.Background };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        layout.Controls.Add(new Label { Text = existing is null ? "Новый студент" : "Обновить образцы лица", Dock = DockStyle.Fill, ForeColor = Theme.Text, Font = Theme.Font(20, FontStyle.Bold) }, 0, 0);
        layout.Controls.Add(FormStyle.Caption("ФИО"), 0, 1); layout.Controls.Add(nameBox, 0, 2);
        layout.Controls.Add(FormStyle.Caption("ГРУППА"), 0, 3); layout.Controls.Add(groupBox, 0, 4);
        var video = new SmoothPanel { Dock = DockStyle.Fill, Padding = new Padding(6) }; video.Controls.Add(preview); layout.Controls.Add(video, 0, 5);
        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = Theme.Background };
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        feedback.Dock = DockStyle.Fill; bottom.Controls.Add(feedback, 0, 0);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Background };
        var captureButton = new ModernButton { Text = "Снять образец", Width = 156, Primary = true };
        var save = new ModernButton { Text = "Сохранить", Width = 130 };
        var cancel = new ModernButton { Text = "Отмена", Width = 110 };
        captureButton.Click += (_, _) => CaptureSample();
        save.Click += (_, _) => Save();
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        actions.Controls.Add(captureButton); actions.Controls.Add(save); actions.Controls.Add(cancel);
        bottom.Controls.Add(actions, 0, 1); layout.Controls.Add(bottom, 0, 6);
        Controls.Add(layout);
        timer.Tick += (_, _) => ReadFrame();
        Shown += (_, _) => OpenCamera();
    }

    private void OpenCamera()
    {
        try
        {
            capture = new VideoCapture(0, VideoCaptureAPIs.DSHOW);
            if (!capture.IsOpened()) { capture.Dispose(); capture = new VideoCapture(0); }
            if (!capture.IsOpened()) throw new InvalidOperationException("Камера не найдена или занята.");
            capture.FrameWidth = 800; capture.FrameHeight = 500;
            engine = new FaceEngine();
            timer.Start();
        }
        catch (Exception ex) { feedback.Text = ex.Message; }
    }

    private void ReadFrame()
    {
        if (capture is null || engine is null) return;
        using var frame = new Mat();
        if (!capture.Read(frame) || frame.Empty()) return;
        var faces = engine.Detect(frame);
        latestFace = faces.Count == 1 ? faces[0] : null;
        latestFrame?.Dispose(); latestFrame = frame.Clone();
        var bitmap = BitmapConverter.ToBitmap(frame);
        using (var g = Graphics.FromImage(bitmap))
        using (var pen = new Pen(faces.Count == 1 ? Theme.Green : Theme.Red, 3))
            foreach (var face in faces) g.DrawRectangle(pen, face.X, face.Y, face.Width, face.Height);
        var old = preview.Image; preview.Image = bitmap; old?.Dispose();
    }

    private void CaptureSample()
    {
        if (latestFace is not { } face || latestFrame is null)
        { feedback.Text = "Для снимка в кадре должно быть ровно одно хорошо видимое лицо."; return; }
        if (samples.Count >= 3) { feedback.Text = "Три образца уже получены. Сохраните студента."; return; }
        try { samples.Add(engine!.CaptureSample(latestFrame, requireSingle: true)); }
        catch (Exception ex) { feedback.Text = ex.Message; return; }
        feedback.Text = samples.Count switch
        {
            1 => "1/3 · Теперь немного поверните голову влево.",
            2 => "2/3 · Теперь немного поверните голову вправо.",
            _ => "3/3 · Готово, сохраните образцы."
        };
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(nameBox.Text) || string.IsNullOrWhiteSpace(groupBox.Text))
        { feedback.Text = "Введите ФИО и группу."; return; }
        if (samples.Count < 3) { feedback.Text = "Снимите три образца лица."; return; }
        if (existing is null) store.AddStudent(nameBox.Text, groupBox.Text, samples);
        else store.ReplaceSamples(existing.Id, samples);
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        timer.Stop(); timer.Dispose(); capture?.Dispose(); engine?.Dispose(); latestFrame?.Dispose(); preview.Image?.Dispose();
        base.OnFormClosed(e);
    }
}

internal sealed class LessonForm : Form
{
    private readonly AttendanceStore store;
    private readonly TextBox subject = FormStyle.Input("Например, Математика");
    private readonly TextBox group = FormStyle.Input("Например, ИС-21");
    private readonly ComboBox day = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, BackColor = Theme.SurfaceHover, ForeColor = Theme.Text, FlatStyle = FlatStyle.Flat, Font = Theme.Font(11) };
    private readonly TextBox start = FormStyle.Input("09:00");
    private readonly TextBox end = FormStyle.Input("10:00");

    public LessonForm(AttendanceStore store)
    {
        this.store = store;
        FormStyle.Dialog(this, "Новое занятие", new System.Drawing.Size(480, 490));
        group.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        group.AutoCompleteSource = AutoCompleteSource.CustomSource;
        group.AutoCompleteCustomSource.AddRange(store.Students().Select(s => s.GroupName).Distinct().ToArray());
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), RowCount = 11, BackColor = Theme.Background };
        for (var i = 0; i < 10; i++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, i % 2 == 0 ? 26 : 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        day.Items.AddRange(["Понедельник", "Вторник", "Среда", "Четверг", "Пятница", "Суббота", "Воскресенье"]);
        day.SelectedIndex = ((int)DateTime.Today.DayOfWeek + 6) % 7;
        start.Text = "09:00"; end.Text = "10:00";
        layout.Controls.Add(FormStyle.Caption("ПРЕДМЕТ"), 0, 0); layout.Controls.Add(subject, 0, 1);
        layout.Controls.Add(FormStyle.Caption("ГРУППА"), 0, 2); layout.Controls.Add(group, 0, 3);
        layout.Controls.Add(FormStyle.Caption("ДЕНЬ НЕДЕЛИ"), 0, 4); layout.Controls.Add(day, 0, 5);
        layout.Controls.Add(FormStyle.Caption("НАЧАЛО"), 0, 6); layout.Controls.Add(start, 0, 7);
        layout.Controls.Add(FormStyle.Caption("КОНЕЦ"), 0, 8); layout.Controls.Add(end, 0, 9);
        var save = new ModernButton { Text = "Сохранить занятие", Primary = true, Width = 190 };
        save.Click += (_, _) => Save();
        layout.Controls.Add(save, 0, 10);
        Controls.Add(layout);
    }

    private void Save()
    {
        var validStart = TimeSpan.TryParseExact(start.Text.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out var startTime);
        var validEnd = TimeSpan.TryParseExact(end.Text.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out var endTime);
        if (string.IsNullOrWhiteSpace(subject.Text) || string.IsNullOrWhiteSpace(group.Text) || !validStart || !validEnd || endTime <= startTime)
        { MessageBox.Show(this, "Укажите предмет, группу и корректное время."); return; }
        var weekday = (DayOfWeek)((day.SelectedIndex + 1) % 7);
        store.AddLesson(subject.Text, group.Text, weekday, startTime, endTime);
        DialogResult = DialogResult.OK; Close();
    }
}
