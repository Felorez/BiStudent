using BiStudent.Contracts;

namespace BiStudent;

internal sealed class FaceSubmissionsDialog : DarkForm
{
    private readonly ApiClient api;
    private readonly DataGridView grid = Ui.Grid();
    private readonly PictureBox[] images = new PictureBox[3];
    private readonly Label name = Ui.Label("Новых заявок нет", 17, Theme.Text, FontStyle.Bold);
    private readonly Label details = Ui.Label("", 10, Theme.Yellow);
    private readonly Label error = Ui.Label("", 9, Theme.Red);
    private readonly ModernButton approve = new() { Text = "Подтвердить", Primary = true, Width = 160 };
    private readonly ModernButton reject = new() { Text = "Отклонить…", Width = 160 };
    private int imageVersion;
    private int? displayedSubmissionId;
    private bool filling;
    private bool busy;

    public bool Changed { get; private set; }

    public FaceSubmissionsDialog(ApiClient api)
    {
        this.api = api;
        Text = "Заявки на регистрацию лица";
        ClientSize = new Size(1140, 620);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;

        var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(18), BackColor = Theme.Background };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        var left = new CardPanel("Заявки") { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 14, 0) };
        grid.AutoGenerateColumns = false;
        grid.RowTemplate.Height = 52;
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "ФИО", FillWeight = 40 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Группа", FillWeight = 18 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Получена", FillWeight = 18 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Примечание", FillWeight = 40 });
        left.Body.Controls.Add(grid);

        var right = new CardPanel("Снимки") { Dock = DockStyle.Fill, Margin = Padding.Empty };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, BackColor = Theme.Surface };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        layout.Controls.Add(name, 0, 0);
        layout.Controls.Add(details, 0, 1);
        var photos = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, BackColor = Theme.Surface };
        for (var i = 0; i < 3; i++)
        {
            photos.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
            var holder = new Panel { Dock = DockStyle.Fill, BackColor = Theme.SurfaceHover, Margin = new Padding(0, 0, 8, 0) };
            images[i] = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.SurfaceHover };
            var caption = Ui.Label(new[] { "Прямо", "Влево", "Вправо" }[i], 9, Theme.Muted);
            caption.Dock = DockStyle.Bottom; caption.Height = 28; caption.TextAlign = ContentAlignment.MiddleCenter;
            holder.Controls.Add(images[i]); holder.Controls.Add(caption);
            photos.Controls.Add(holder, i, 0);
        }
        layout.Controls.Add(photos, 0, 2);
        var actions = Ui.Row();
        actions.Controls.Add(approve); actions.Controls.Add(reject);
        layout.Controls.Add(actions, 0, 3);
        right.Body.Controls.Add(layout);
        columns.Controls.Add(left, 0, 0); columns.Controls.Add(right, 1, 0);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 58, BackColor = Theme.Background, Padding = new Padding(18, 6, 18, 6) };
        var close = new ModernButton { Text = "Закрыть", Width = 130, Dock = DockStyle.Right };
        close.Click += (_, _) => Close();
        error.Dock = DockStyle.Fill;
        footer.Controls.Add(error); footer.Controls.Add(close);
        Controls.Add(columns); Controls.Add(footer);
        CancelButton = close;
        grid.CurrentCellChanged += async (_, _) => { if (!filling) await LoadImagesAsync(); };
        approve.Click += async (_, _) => await DecideAsync(true);
        reject.Click += async (_, _) => await DecideAsync(false);
        Shown += async (_, _) => await ReloadAsync();
        SetBusy(false);
    }

    private FaceSubmissionDto? Selected() => grid.CurrentRow?.Tag as FaceSubmissionDto;

    private void SetBusy(bool value)
    {
        busy = value;
        grid.Enabled = !value;
        approve.Enabled = reject.Enabled = !value && Selected()?.Id == displayedSubmissionId && displayedSubmissionId is not null;
        UseWaitCursor = value;
    }

    private void ClearImages()
    {
        displayedSubmissionId = null;
        foreach (var box in images)
        {
            var old = box.Image;
            box.Image = null;
            old?.Dispose();
        }
    }

    private async Task ReloadAsync()
    {
        imageVersion++;
        ClearImages();
        SetBusy(true);
        error.Text = "";
        try
        {
            var submissions = await api.FaceSubmissionsAsync();
            if (IsDisposed) return;
            filling = true;
            grid.Rows.Clear();
            foreach (var item in submissions)
            {
                var note = item.SimilarTo is { } similar ? $"похоже на {similar} ({UiFormat.Percent(item.SimilarScore * 100)})"
                    : item.ReplacesExisting ? "заменит текущие образцы" : "";
                var index = grid.Rows.Add(item.StudentName, item.GroupName, item.CreatedAt.ToString("dd.MM HH:mm"), note);
                grid.Rows[index].Tag = item;
                if (item.SimilarTo is not null) grid.Rows[index].Cells[3].Style.ForeColor = Theme.Yellow;
            }
            name.Text = submissions.Count == 0 ? "Новых заявок нет" : "";
            if (grid.Rows.Count > 0) grid.CurrentCell = grid.Rows[0].Cells[0];
            filling = false;
        }
        catch (ApiException ex) { if (!IsDisposed) error.Text = ex.Message; }
        finally { filling = false; if (!IsDisposed) SetBusy(false); }
        if (!IsDisposed && Selected() is not null) await LoadImagesAsync();
    }

    private async Task LoadImagesAsync()
    {
        var version = ++imageVersion;
        ClearImages();
        var item = Selected();
        name.Text = item?.StudentName ?? "Новых заявок нет";
        details.Text = item is null ? "" : $"{item.GroupName} · {item.CreatedAt:dd.MM.yyyy HH:mm}" +
            (item.ReplacesExisting ? " · заменит текущие образцы" : "") +
            (item.SimilarTo is { } similar ? $" · похоже на {similar} ({UiFormat.Percent(item.SimilarScore * 100)})" : "");
        SetBusy(busy);
        if (item is null) return;
        try
        {
            var bytes = await Task.WhenAll(Enumerable.Range(0, 3).Select(index => api.FaceSubmissionImageAsync(item.Id, index)));
            if (IsDisposed || version != imageVersion || Selected()?.Id != item.Id) return;
            for (var i = 0; i < images.Length; i++)
            {
                using var stream = new MemoryStream(bytes[i]);
                using var image = Image.FromStream(stream);
                images[i].Image = new Bitmap(image);
            }
            displayedSubmissionId = item.Id;
            SetBusy(busy);
        }
        catch (Exception ex) when (ex is ApiException or ArgumentException or System.Runtime.InteropServices.ExternalException)
        {
            if (!IsDisposed && version == imageVersion) { ClearImages(); error.Text = ex.Message; }
        }
    }

    private async Task DecideAsync(bool accepted)
    {
        if (busy || Selected() is not { } item || displayedSubmissionId != item.Id) return;
        string? reason = null;
        if (!accepted)
        {
            reason = Ui.Prompt(this, "Отклонить заявку", "Причина (необязательно)");
            if (reason is null) return;
        }
        if (Selected()?.Id != displayedSubmissionId || displayedSubmissionId != item.Id) return;
        SetBusy(true);
        error.Text = "";
        try
        {
            if (accepted) await api.ApproveSubmissionAsync(item.Id);
            else await api.RejectSubmissionAsync(item.Id, reason);
            if (IsDisposed) return;
            Changed = true;
            Ui.Toast(this, accepted ? "Регистрация подтверждена" : "Заявка отклонена");
            await ReloadAsync();
        }
        catch (ApiException ex) { if (!IsDisposed) error.Text = ex.Message; }
        finally { if (!IsDisposed) SetBusy(false); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { imageVersion++; ClearImages(); }
        base.Dispose(disposing);
    }
}
