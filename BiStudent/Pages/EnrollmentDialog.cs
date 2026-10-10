using System.Collections;
using System.Reflection;
using BiStudent.Contracts;
using OpenCvSharp;

namespace BiStudent;

internal sealed class EnrollmentDialog : DarkForm
{
    private static readonly string[] Prompts = ["Смотрите прямо", "Немного влево", "Немного вправо"];
    private readonly ApiClient api;
    private readonly StudentDto student;
    private readonly PictureBox preview = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.Sidebar };
    private readonly PictureBox[] thumbs = new PictureBox[3];
    private readonly Label[] steps = new Label[3];
    private readonly Label hint = Ui.Label("", 9.5f, Theme.Muted);
    private readonly Label banner = Ui.Label("", 9.5f, Theme.Red, FontStyle.Bold);
    private readonly ModernButton take = new() { Text = "Снять", Primary = true, Width = 125 };
    private readonly ModernButton retake = new() { Text = "Переснять последний", Width = 190 };
    private readonly ModernButton save = new() { Text = "Сохранить образцы", Width = 200 };
    private readonly List<byte[]> shots = [];
    private readonly object frameLock = new();
    private CancellationTokenSource? cameraCts;
    private Task? cameraTask;
    private Mat? lastFrame;
    private Bitmap? queuedBitmap;
    private object? engine;
    private MethodInfo? detect;
    private int faceCount;
    private int pendingFrame;
    private DateTime lastFrameAt;
    private bool detectorReady;
    private bool saving;
    private bool stopping;
    private bool closingAfterStop;
    private DialogResult closeResult;

    public EnrollmentDialog(ApiClient api, StudentDto student)
    {
        this.api = api;
        this.student = student;
        Text = $"Регистрация лица · {student.FullName}";
        ClientSize = new System.Drawing.Size(980, 640);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;

        var left = new SmoothPanel { Dock = DockStyle.Fill, Padding = new Padding(12), Margin = new Padding(0, 0, 16, 0) };
        left.Controls.Add(preview);
        var right = new SmoothPanel { Dock = DockStyle.Fill, Padding = new Padding(18) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 10, BackColor = Color.Transparent };
        foreach (var height in new[] { 38, 36, 36, 36, 112, 48, 50, 58, 58, 77 })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.Controls.Add(Ui.Label(student.FullName, 14, Theme.Text, FontStyle.Bold), 0, 0);
        for (var i = 0; i < 3; i++)
        {
            steps[i] = Ui.Label("", 10, Theme.Muted);
            layout.Controls.Add(steps[i], 0, i + 1);
        }
        var previews = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, BackColor = Color.Transparent };
        for (var i = 0; i < 3; i++)
        {
            previews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
            thumbs[i] = new PictureBox
            {
                Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.SurfaceHover,
                Margin = new Padding(0, 5, 8, 5)
            };
            previews.Controls.Add(thumbs[i], i, 0);
        }
        layout.Controls.Add(previews, 0, 4);
        hint.TextAlign = ContentAlignment.TopLeft;
        layout.Controls.Add(hint, 0, 5);
        banner.TextAlign = ContentAlignment.TopLeft;
        banner.AutoEllipsis = true;
        banner.Visible = false;
        layout.Controls.Add(banner, 0, 6);
        var actions = Ui.Row();
        actions.Controls.Add(take); actions.Controls.Add(retake);
        layout.Controls.Add(actions, 0, 7);
        layout.Controls.Add(save, 0, 8);
        var consent = Ui.Label("Снимайте с согласия студента. На сервере сохраняются только зашифрованные образцы лица 112×112.", 8.5f, Theme.Muted);
        consent.TextAlign = ContentAlignment.TopLeft;
        layout.Controls.Add(consent, 0, 9);
        right.Controls.Add(layout);
        var columns = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(18),
            BackColor = Theme.Background
        };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        columns.Controls.Add(left, 0, 0); columns.Controls.Add(right, 1, 0);
        Controls.Add(columns);

        take.Click += (_, _) => TakeShot();
        retake.Click += (_, _) => Retake();
        save.Click += async (_, _) => await SaveAsync();
        Shown += (_, _) =>
        {
            cameraCts = new CancellationTokenSource();
            cameraTask = Task.Run(() => CameraLoopAsync(cameraCts.Token), cameraCts.Token);
            UpdateState();
        };
    }

    private void CreateDetector()
    {
        try
        {
            var type = typeof(EnrollResultDto).Assembly.GetType("BiStudent.FaceEngine");
            if (type is null) return;
            var models = Directory.EnumerateFiles(AppContext.BaseDirectory, "*.onnx", SearchOption.AllDirectories).ToArray();
            var yunet = models.FirstOrDefault(p => p.Contains("yunet", StringComparison.OrdinalIgnoreCase) ||
                                                   p.Contains("detection", StringComparison.OrdinalIgnoreCase));
            var sface = models.FirstOrDefault(p => p.Contains("sface", StringComparison.OrdinalIgnoreCase) ||
                                                   p.Contains("recognition", StringComparison.OrdinalIgnoreCase));
            foreach (var constructor in type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                         .OrderBy(c => c.GetParameters().Length))
            {
                var parameters = constructor.GetParameters();
                var arguments = new object?[parameters.Length];
                var valid = true;
                for (var i = 0; i < parameters.Length; i++)
                {
                    var parameter = parameters[i];
                    if (parameter.ParameterType == typeof(string))
                    {
                        var recognition = parameter.Name?.Contains("recogn", StringComparison.OrdinalIgnoreCase) == true ||
                                          parameter.Name?.Contains("sface", StringComparison.OrdinalIgnoreCase) == true || i > 0;
                        arguments[i] = recognition ? sface : yunet;
                        if (arguments[i] is null) valid = false;
                    }
                    else if (parameter.HasDefaultValue) arguments[i] = parameter.DefaultValue;
                    else valid = false;
                }
                if (!valid) continue;
                try { engine = constructor.Invoke(arguments); break; }
                catch { }
            }
            if (engine is null) return;
            detect = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "Detect" && m.GetParameters() is { Length: > 0 } p &&
                                     p[0].ParameterType.IsAssignableFrom(typeof(Mat)) &&
                                     p.Skip(1).All(x => x.HasDefaultValue));
            if (detect is null) { (engine as IDisposable)?.Dispose(); engine = null; }
        }
        catch { (engine as IDisposable)?.Dispose(); engine = null; detect = null; }
    }

    private async Task CameraLoopAsync(CancellationToken token)
    {
        VideoCapture? camera = null;
        var noFrameSince = DateTime.UtcNow;
        var nextRetry = DateTime.MinValue;
        try
        {
            CreateDetector();
            var ready = engine is not null;
            PostUi(() => { if (!IsDisposed && !token.IsCancellationRequested) { detectorReady = ready; UpdateState(); } });
            while (!token.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;
                if (camera is null && now >= nextRetry)
                {
                    try
                    {
                        var devices = CameraDevices.List();
                        var index = CameraDevices.ResolveIndex(ClientSettings.Load(), devices);
                        camera = CameraDevices.Open(index, 800, 500, allowFallback: true);
                        noFrameSince = DateTime.UtcNow;
                        PostStatus("Подключение к камере…", token);
                    }
                    catch (Exception ex)
                    {
                        camera?.Dispose();
                        camera = null;
                        nextRetry = DateTime.UtcNow.AddSeconds(2);
                        PostStatus($"Камера недоступна: {ex.Message}. Повторная попытка через 2 секунды.", token);
                    }
                }
                if (camera is not null)
                {
                    try
                    {
                        using var frame = new Mat();
                        if (!camera.Read(frame) || frame.Empty())
                        {
                            if ((DateTime.UtcNow - noFrameSince).TotalSeconds > 3)
                            {
                                camera.Dispose();
                                camera = null;
                                ClearLatestFrame();
                                nextRetry = DateTime.UtcNow.AddSeconds(2);
                                PostStatus("Камера не передаёт кадры. Переподключение через 2 секунды…", token);
                            }
                        }
                        else
                        {
                            noFrameSince = DateTime.UtcNow;
                            var faces = DetectFaces(frame);
                            StoreFrame(frame, faces.Count);
                            if (Interlocked.CompareExchange(ref pendingFrame, 1, 0) == 0)
                            {
                                Bitmap? bitmap = null;
                                var posted = false;
                                try
                                {
                                    Cv2.ImEncode(".bmp", frame, out var bytes);
                                    using var stream = new MemoryStream(bytes);
                                    using var decoded = new Bitmap(stream);
                                    bitmap = new Bitmap(decoded);
                                    using (var graphics = Graphics.FromImage(bitmap))
                                    using (var pen = new Pen(faces.Count == 1 ? Theme.Green : Theme.Yellow, 3))
                                        foreach (var bounds in faces) graphics.DrawRectangle(pen, bounds);
                                    Interlocked.Exchange(ref queuedBitmap, bitmap);
                                    bitmap = null;
                                    var available = engine is not null;
                                    posted = PostUi(() => FrameReady(token, available));
                                }
                                finally
                                {
                                    bitmap?.Dispose();
                                    if (!posted)
                                    {
                                        Interlocked.Exchange(ref queuedBitmap, null)?.Dispose();
                                        Interlocked.Exchange(ref pendingFrame, 0);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex) when (!token.IsCancellationRequested)
                    {
                        camera?.Dispose();
                        camera = null;
                        ClearLatestFrame();
                        nextRetry = DateTime.UtcNow.AddSeconds(2);
                        PostStatus($"Ошибка камеры: {ex.Message}. Переподключение через 2 секунды…", token);
                    }
                }
                await Task.Delay(40, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            camera?.Dispose();
            ClearLatestFrame();
            (engine as IDisposable)?.Dispose();
            engine = null; detect = null;
        }
    }

    private bool PostUi(Action action)
    {
        try { BeginInvoke(action); return true; }
        catch (InvalidOperationException) { return false; }
    }

    private void PostStatus(string message, CancellationToken token)
    {
        PostUi(() =>
        {
            if (IsDisposed || token.IsCancellationRequested) return;
            hint.Text = message;
            UpdateState();
        });
    }

    private void StoreFrame(Mat frame, int count)
    {
        lock (frameLock)
        {
            lastFrame?.Dispose();
            lastFrame = frame.Clone();
            lastFrameAt = DateTime.UtcNow;
            faceCount = count;
        }
    }

    private void ClearLatestFrame()
    {
        lock (frameLock)
        {
            lastFrame?.Dispose();
            lastFrame = null;
            lastFrameAt = default;
            faceCount = 0;
        }
    }

    private void FrameReady(CancellationToken token, bool available)
    {
        var bitmap = Interlocked.Exchange(ref queuedBitmap, null);
        Interlocked.Exchange(ref pendingFrame, 0);
        if (bitmap is null) return;
        if (IsDisposed || token.IsCancellationRequested) { bitmap.Dispose(); return; }
        detectorReady = available;
        var old = preview.Image;
        preview.Image = bitmap;
        old?.Dispose();
        UpdateState();
    }

    private List<Rectangle> DetectFaces(Mat frame)
    {
        var result = new List<Rectangle>();
        if (engine is null || detect is null) return result;
        try
        {
            var parameters = detect.GetParameters();
            var arguments = new object?[parameters.Length];
            arguments[0] = frame;
            for (var i = 1; i < parameters.Length; i++) arguments[i] = parameters[i].DefaultValue;
            if (detect.Invoke(engine, arguments) is IEnumerable faces)
            {
                foreach (var face in faces)
                {
                    if (face is null) continue;
                    result.Add(FaceBounds(face) ?? Rectangle.Empty);
                }
            }
        }
        catch
        {
            (engine as IDisposable)?.Dispose();
            engine = null;
            detect = null;
            result.Clear();
        }
        return result;
    }

    private static Rectangle? FaceBounds(object face)
    {
        if (face is Rectangle rectangle) return rectangle;
        if (face is Rect rect) return new Rectangle(rect.X, rect.Y, rect.Width, rect.Height);
        var nested = Member(face, "Rect") ?? Member(face, "Bounds") ?? Member(face, "Box");
        if (nested is not null && !ReferenceEquals(nested, face)) return FaceBounds(nested);
        var x = Member(face, "X"); var y = Member(face, "Y");
        var w = Member(face, "Width") ?? Member(face, "W");
        var h = Member(face, "Height") ?? Member(face, "H");
        return x is null || y is null || w is null || h is null ? null
            : new Rectangle(Convert.ToInt32(x), Convert.ToInt32(y), Convert.ToInt32(w), Convert.ToInt32(h));
    }

    private static object? Member(object value, string name) =>
        value.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(value) ??
        value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(value);

    private void UpdateState()
    {
        bool fresh;
        int count;
        lock (frameLock)
        {
            fresh = lastFrame is not null && (DateTime.UtcNow - lastFrameAt).TotalMilliseconds < 500;
            count = faceCount;
        }
        take.Enabled = !saving && !stopping && shots.Count < 3 && fresh && detectorReady && count == 1;
        retake.Enabled = !saving && !stopping && shots.Count > 0;
        save.Enabled = !saving && !stopping && shots.Count == 3;
        for (var i = 0; i < 3; i++)
        {
            steps[i].Text = i < shots.Count ? $"✓  {i + 1}/3  {Prompts[i]}"
                : $"{i + 1}/3  {Prompts[i]}";
            steps[i].ForeColor = i < shots.Count ? Theme.Green : i == shots.Count ? Theme.AccentHover : Theme.Muted;
        }
        if (saving) { hint.Text = "Сохранение образцов…"; return; }
        if (!detectorReady) { hint.Text = "Проверка лица недоступна: не удалось загрузить модель."; return; }
        if (!fresh) return;
        hint.Text = shots.Count == 3 ? "Три снимка готовы. Сохраните образцы."
            : count == 0 ? "Наведите камеру так, чтобы в кадре было ровно одно лицо."
            : count > 1 ? "В кадре несколько лиц. Оставьте одного студента."
            : $"Готово: {Prompts[shots.Count].ToLowerInvariant()}. Нажмите «Снять».";
    }

    private void TakeShot()
    {
        if (!take.Enabled) return;
        Mat frame;
        lock (frameLock)
        {
            if (lastFrame is null || faceCount != 1 || (DateTime.UtcNow - lastFrameAt).TotalMilliseconds >= 500) return;
            frame = lastFrame.Clone();
        }
        using (frame)
        {
            Cv2.ImEncode(".jpg", frame, out var jpeg, new ImageEncodingParam(ImwriteFlags.JpegQuality, 92));
            shots.Add(jpeg);
            using var stream = new MemoryStream(jpeg);
            using var decoded = new Bitmap(stream);
            thumbs[shots.Count - 1].Image = new Bitmap(decoded);
        }
        banner.Visible = false;
        banner.Text = "";
        UpdateState();
    }

    private void Retake()
    {
        if (saving || shots.Count == 0) return;
        shots.RemoveAt(shots.Count - 1);
        var thumb = thumbs[shots.Count];
        var image = thumb.Image;
        thumb.Image = null;
        image?.Dispose();
        UpdateState();
    }

    private void ResetShots()
    {
        shots.Clear();
        foreach (var thumb in thumbs)
        {
            var image = thumb.Image;
            thumb.Image = null;
            image?.Dispose();
        }
        UpdateState();
    }

    private async Task SaveAsync()
    {
        if (saving || shots.Count != 3) return;
        saving = true;
        take.Enabled = false; retake.Enabled = false; save.Enabled = false;
        UseWaitCursor = true;
        UpdateState();
        try
        {
            var result = await api.EnrollAsync(student.Id, shots.ToList());
            if (IsDisposed) return;
            if (result.Ok) { DialogResult = DialogResult.OK; Close(); }
            else { ResetShots(); ShowError(result.Message); }
        }
        catch (ApiException ex) { if (!IsDisposed) { ResetShots(); ShowError(ex.Message); } }
        finally
        {
            saving = false;
            if (!IsDisposed) { UseWaitCursor = false; UpdateState(); }
        }
    }

    private void ShowError(string message)
    {
        banner.Text = $"⚠  {message}";
        banner.Visible = true;
    }

    private void ClearFrame()
    {
        ClearLatestFrame();
        Interlocked.Exchange(ref queuedBitmap, null)?.Dispose();
        var image = preview.Image;
        preview.Image = null;
        image?.Dispose();
    }

    private async Task StopAndCloseAsync()
    {
        cameraCts?.Cancel();
        var finished = true;
        try { finished = await Ui.SettleAsync(cameraTask); }
        finally
        {
            cameraTask = null;
            if (finished) cameraCts?.Dispose();
            cameraCts = null;
            ClearFrame();
            closingAfterStop = true;
            stopping = false;
            if (!IsDisposed) { DialogResult = closeResult; Close(); }
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!closingAfterStop && cameraTask is not null)
        {
            e.Cancel = true;
            if (!stopping)
            {
                stopping = true;
                closeResult = DialogResult;
                _ = StopAndCloseAsync();
            }
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            cameraCts?.Cancel();
            ClearFrame();
            foreach (var thumb in thumbs)
            {
                var image = thumb?.Image;
                if (thumb is not null) thumb.Image = null;
                image?.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}
