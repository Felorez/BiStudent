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
    private readonly byte[]?[] shots = new byte[]?[3];
    private readonly object frameLock = new();
    private readonly object engineLock = new();
    private int? retakeSlot;
    private CancellationTokenSource? cameraCts;
    private Task? cameraTask;
    private Mat? lastFrame;
    private Bitmap? queuedBitmap;
    private FaceEngine? engine;
    private SampleQuality? quality;
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
        var tips = new ToolTip();
        var previews = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, BackColor = Color.Transparent };
        for (var i = 0; i < 3; i++)
        {
            previews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
            thumbs[i] = new PictureBox
            {
                Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.SurfaceHover,
                Margin = new Padding(0, 5, 8, 5)
            };
            var index = i;
            thumbs[i].Click += (_, _) => RetakeAt(index);
            tips.SetToolTip(thumbs[i], "Нажмите, чтобы переснять этот снимок");
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
        try { engine = new FaceEngine(); }
        catch { engine = null; }
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
                            StoreFrame(frame, faces.Count, Assess(frame, faces.Count));
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
            lock (engineLock)
            {
                engine?.Dispose();
                engine = null;
            }
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

    private void StoreFrame(Mat frame, int count, SampleQuality? check)
    {
        lock (frameLock)
        {
            lastFrame?.Dispose();
            lastFrame = frame.Clone();
            lastFrameAt = DateTime.UtcNow;
            faceCount = count;
            quality = check;
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
            quality = null;
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
        lock (engineLock)
        {
            if (engine is null) return [];
            try { return engine.Detect(frame).Select(r => new Rectangle(r.X, r.Y, r.Width, r.Height)).ToList(); }
            catch
            {
                engine.Dispose();
                engine = null;
                return [];
            }
        }
    }

    private SampleQuality? Assess(Mat frame, int faces)
    {
        lock (engineLock)
        {
            if (engine is null) return null;
            try { return engine.AssessFrame(frame); }
            catch { return null; }
        }
    }

    private int Filled => shots.Count(s => s is not null);
    private int NextSlot => retakeSlot is int slot && shots[slot] is null ? slot : Array.IndexOf(shots, null);

    private void UpdateState()
    {
        bool fresh;
        int count;
        SampleQuality? check;
        lock (frameLock)
        {
            fresh = lastFrame is not null && (DateTime.UtcNow - lastFrameAt).TotalMilliseconds < 500;
            count = faceCount;
            check = quality;
        }
        var next = NextSlot;
        take.Enabled = !saving && !stopping && next >= 0 && fresh && detectorReady && count == 1 && check is { Ok: true };
        retake.Enabled = !saving && !stopping && Filled > 0;
        save.Enabled = !saving && !stopping && Filled == 3;
        for (var i = 0; i < 3; i++)
        {
            steps[i].Text = shots[i] is not null ? $"✓  {i + 1}/3  {Prompts[i]}" : $"{i + 1}/3  {Prompts[i]}";
            steps[i].ForeColor = shots[i] is not null ? Theme.Green : i == next ? Theme.AccentHover : Theme.Muted;
            thumbs[i].Cursor = shots[i] is not null && !saving ? Cursors.Hand : Cursors.Default;
        }
        if (saving) { hint.Text = "Сохранение образцов…"; return; }
        if (!detectorReady) { hint.Text = "Проверка лица недоступна: не удалось загрузить модель."; return; }
        if (!fresh) return;
        hint.ForeColor = Theme.Muted;
        if (next < 0) { hint.Text = "Три снимка готовы. Сохраните образцы или нажмите на снимок, чтобы переснять его."; return; }
        if (check is { Ok: false, Problem: { } problem })
        {
            hint.ForeColor = Theme.Yellow;
            hint.Text = $"{problem}.";
            return;
        }
        if (count == 0) { hint.Text = "Наведите камеру так, чтобы в кадре было ровно одно лицо."; return; }
        if (count > 1) { hint.Text = "В кадре несколько лиц. Оставьте одного студента."; return; }
        if (check is null) { hint.ForeColor = Theme.Yellow; hint.Text = "Не удалось оценить кадр — немного измените положение головы."; return; }
        hint.Text = $"Кадр хороший: {Prompts[next].ToLowerInvariant()}. Нажмите «Снять».";
    }

    private void TakeShot()
    {
        if (!take.Enabled) return;
        var slot = NextSlot;
        if (slot < 0) return;
        Mat frame;
        lock (frameLock)
        {
            if (lastFrame is null || faceCount != 1 || quality is not { Ok: true } || (DateTime.UtcNow - lastFrameAt).TotalMilliseconds >= 500) return;
            frame = lastFrame.Clone();
        }
        using (frame)
        {
            Cv2.ImEncode(".jpg", frame, out var jpeg, new ImageEncodingParam(ImwriteFlags.JpegQuality, 92));
            shots[slot] = jpeg;
            if (retakeSlot == slot) retakeSlot = null;
            using var stream = new MemoryStream(jpeg);
            using var decoded = new Bitmap(stream);
            var old = thumbs[slot].Image;
            thumbs[slot].Image = new Bitmap(decoded);
            old?.Dispose();
        }
        banner.Visible = false;
        banner.Text = "";
        UpdateState();
    }

    private void Retake()
    {
        var last = Array.FindLastIndex(shots, s => s is not null);
        if (last >= 0) RetakeAt(last);
    }

    private void RetakeAt(int index)
    {
        if (saving || shots[index] is null) return;
        shots[index] = null;
        retakeSlot = index;
        var image = thumbs[index].Image;
        thumbs[index].Image = null;
        image?.Dispose();
        UpdateState();
    }

    private void ResetShots()
    {
        Array.Clear(shots);
        retakeSlot = null;
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
        if (saving || Filled != 3) return;
        saving = true;
        take.Enabled = false; retake.Enabled = false; save.Enabled = false;
        UseWaitCursor = true;
        UpdateState();
        try
        {
            var images = shots.Select(s => s!).ToList();
            var result = await api.EnrollAsync(student.Id, images);
            if (IsDisposed) return;
            if (!result.Ok && result.SimilarTo is not null &&
                Ui.Confirm(this, "Похожее лицо", result.Message, "Сохранить всё равно"))
                result = await api.EnrollAsync(student.Id, images, allowSimilar: true);
            if (IsDisposed) return;
            if (result.Ok) { DialogResult = DialogResult.OK; Close(); return; }
            if (result.Problems is { Count: > 0 } problems)
            {
                ShowError($"{result.Message} — подробности в окне со списком.");
                Ui.Message(this, "Какие снимки переснять", string.Join(Environment.NewLine, problems));
            }
            else if (result.SimilarTo is null) { ResetShots(); ShowError(result.Message); }
        }
        catch (ApiException ex) { if (!IsDisposed) ShowError(ex.Message); }
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
