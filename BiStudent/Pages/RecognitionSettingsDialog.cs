using BiStudent.Contracts;

namespace BiStudent;

internal sealed class RecognitionSettingsDialog : DarkForm
{
    private readonly ApiClient api;
    private readonly NumericUpDown threshold = Number(30, 70);
    private readonly NumericUpDown margin = Number(0, 20);
    private readonly NumericUpDown frames = Number(1, 10);
    private readonly Label error = Ui.Label("", 9.5f, Theme.Red);
    private readonly ModernButton save = new() { Text = "Сохранить", Primary = true, Width = 140 };

    public RecognitionSettingsDto? SavedSettings { get; private set; }
    private bool busy = true;
    private bool loaded;
    private ModernButton? cancelButton;
    private ModernButton? defaultsButton;

    public RecognitionSettingsDialog(ApiClient api)
    {
        this.api = api;
        Text = "Настройки распознавания";
        ClientSize = new Size(620, 470);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;

        var heading = Ui.Label(Text, 15, Theme.Text, FontStyle.Bold);
        heading.Dock = DockStyle.Top;
        heading.Height = 56;
        heading.Padding = new Padding(24, 10, 0, 0);

        var content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Padding = new Padding(24, 8, 24, 8) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, BackColor = Theme.Background };
        for (var i = 0; i < 3; i++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(Field("Порог сходства", "Выше — меньше ложных отметок, но чаще «Неизвестный»", threshold, true), 0, 0);
        layout.Controls.Add(Field("Отрыв от второго кандидата", "Защищает от путаницы похожих студентов", margin, true), 0, 1);
        layout.Controls.Add(Field("Кадров подряд для отметки", "Больше — надёжнее, но отметка ставится дольше", frames, false), 0, 2);
        error.TextAlign = ContentAlignment.TopLeft;
        layout.Controls.Add(error, 0, 3);
        content.Controls.Add(layout);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 58, FlowDirection = FlowDirection.RightToLeft,
            BackColor = Theme.Background, Padding = new Padding(16, 8, 6, 8)
        };
        var cancel = new ModernButton { Text = "Отмена", Width = 120 };
        var defaults = new ModernButton { Text = "По умолчанию", Width = 165 };
        save.Enabled = false;
        cancelButton = cancel; defaultsButton = defaults;
        SetEditing(false);
        save.Click += async (_, _) => await SaveAsync();
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        defaults.Click += (_, _) => { SetValues(new RecognitionSettingsDto(FaceEngine.DefaultMatchThreshold, FaceEngine.DefaultMinimumMargin, 3)); loaded = true; SetEditing(true); };
        buttons.Controls.Add(save); buttons.Controls.Add(cancel); buttons.Controls.Add(defaults);
        AcceptButton = save;
        CancelButton = cancel;
        Controls.Add(content); Controls.Add(buttons); Controls.Add(heading);
        Shown += async (_, _) => await LoadSettingsAsync();
    }

    private static NumericUpDown Number(int minimum, int maximum) => new()
    {
        Minimum = minimum, Maximum = maximum, Value = minimum, DecimalPlaces = 0,
        Increment = 1, BackColor = Theme.SurfaceHover, ForeColor = Theme.Text,
        Font = Theme.Font(11), Width = 112, TextAlign = HorizontalAlignment.Right
    };

    private static Control Field(string caption, string explanation, NumericUpDown input, bool percent)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, BackColor = Theme.Background };
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        row.Controls.Add(Ui.Label(caption, 10, Theme.Text, FontStyle.Bold), 0, 0);
        var value = Ui.Row();
        value.Controls.Add(input);
        if (percent)
        {
            var unit = Ui.Label("%", 10, Theme.Text);
            unit.AutoSize = true; unit.Dock = DockStyle.None; unit.Padding = new Padding(8, 7, 0, 0);
            value.Controls.Add(unit);
        }
        row.Controls.Add(value, 0, 1);
        row.Controls.Add(Ui.Label(explanation, 9, Theme.Muted), 0, 2);
        return row;
    }

    private void SetValues(RecognitionSettingsDto settings)
    {
        threshold.Value = Math.Clamp((decimal)Math.Round(settings.MatchThreshold * 100), threshold.Minimum, threshold.Maximum);
        margin.Value = Math.Clamp((decimal)Math.Round(settings.MinimumMargin * 100), margin.Minimum, margin.Maximum);
        frames.Value = Math.Clamp(settings.FramesInRow, frames.Minimum, frames.Maximum);
        error.Text = "";
    }

    private async Task LoadSettingsAsync()
    {
        try { SetValues(await api.RecognitionSettingsAsync()); loaded = true; }
        catch (ApiException ex) { if (!IsDisposed) error.Text = $"{ex.Message}. Можно задать значения «По умолчанию»."; }
        finally { if (!IsDisposed) SetEditing(true); }
    }

    private async Task SaveAsync()
    {
        SetEditing(false);
        error.Text = "";
        try
        {
            var settings = new RecognitionSettingsDto((double)(threshold.Value / 100m), (double)(margin.Value / 100m), (int)frames.Value);
            SavedSettings = await api.SaveRecognitionSettingsAsync(settings);
            if (IsDisposed) return;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (ApiException ex) { if (!IsDisposed) error.Text = ex.Message; }
        finally { if (!IsDisposed && DialogResult != DialogResult.OK) SetEditing(true); }
    }

    private void SetEditing(bool enabled)
    {
        busy = !enabled;
        threshold.Enabled = margin.Enabled = frames.Enabled = save.Enabled = enabled && loaded;
        if (cancelButton is not null) cancelButton.Enabled = enabled;
        if (defaultsButton is not null) defaultsButton.Enabled = enabled;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (busy && SavedSettings is null && e.CloseReason == CloseReason.UserClosing && DialogResult != DialogResult.OK) e.Cancel = true;
        base.OnFormClosing(e);
    }
}
