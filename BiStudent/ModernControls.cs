using System.Collections.Concurrent;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace BiStudent;

internal static class Theme
{
    public static readonly Color Background = ColorTranslator.FromHtml("#111827");
    public static readonly Color Sidebar = ColorTranslator.FromHtml("#0B1220");
    public static readonly Color Surface = ColorTranslator.FromHtml("#1D2939");
    public static readonly Color SurfaceHover = ColorTranslator.FromHtml("#273548");
    public static readonly Color SurfaceAlt = ColorTranslator.FromHtml("#31425A");
    public static readonly Color Accent = ColorTranslator.FromHtml("#5B8DEF");
    public static readonly Color AccentHover = ColorTranslator.FromHtml("#75A2FA");
    public static readonly Color Text = ColorTranslator.FromHtml("#F4F7FB");
    public static readonly Color Muted = ColorTranslator.FromHtml("#9AAAC0");
    public static readonly Color Line = ColorTranslator.FromHtml("#334155");
    public static readonly Color Green = ColorTranslator.FromHtml("#36CFA5");
    public static readonly Color Red = ColorTranslator.FromHtml("#F27D86");
    public static readonly Color Yellow = ColorTranslator.FromHtml("#F5C451");
    public static readonly Color Violet = ColorTranslator.FromHtml("#A78BFA");
    private static readonly ConcurrentDictionary<(float Size, FontStyle Style), Lazy<Font>> fonts = new();
    public static Font Font(float size, FontStyle style = FontStyle.Regular) => fonts.GetOrAdd((size, style), key => new Lazy<Font>(() => new Font("Segoe UI", key.Size, key.Style))).Value;
    public static Color Mix(Color a, Color b, float amount) => Color.FromArgb((int)(a.R + (b.R - a.R) * amount), (int)(a.G + (b.G - a.G) * amount), (int)(a.B + (b.B - a.B) * amount));
}

internal class DarkForm : Form
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);

    public DarkForm()
    {
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Font(10);
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var enabled = 1;
        if (DwmSetWindowAttribute(Handle, 20, ref enabled, sizeof(int)) != 0)
            DwmSetWindowAttribute(Handle, 19, ref enabled, sizeof(int));
    }
}

internal sealed class ModernButton : Control, IButtonControl
{
    private bool hovered;
    private bool pressed;
    private bool selected;
    private float hoverProgress;
    private readonly System.Windows.Forms.Timer animation;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Primary { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Danger { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Dashed { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? Icon { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected { get => selected; set { selected = value; hoverProgress = value ? 1f : 0f; Invalidate(); } }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ContentAlignment TextAlign { get; set; } = ContentAlignment.MiddleCenter;

    public ModernButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        DoubleBuffered = true;
        BackColor = Color.Transparent;
        Size = new Size(148, 42);
        Margin = new Padding(0, 0, 10, 0);
        Font = Theme.Font(10, FontStyle.Bold);
        ForeColor = Theme.Text;
        Cursor = Cursors.Hand;
        animation = new System.Windows.Forms.Timer { Interval = 16 };
        animation.Tick += (_, _) =>
        {
            var target = hovered || Selected ? 1f : 0f;
            hoverProgress += (target - hoverProgress) * .25f;
            if (Math.Abs(target - hoverProgress) < .02f) { hoverProgress = target; animation.Stop(); }
            Invalidate();
        };
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        if (!Enabled) { hovered = false; pressed = false; hoverProgress = Selected ? 1f : 0f; animation.Stop(); }
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnMouseEnter(EventArgs e) { hovered = true; animation.Start(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; pressed = false; animation.Start(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var onSurface = SurfaceBehind() == Theme.Surface;
        var plain = onSurface ? Theme.SurfaceHover : Theme.Surface;
        var plainHover = onSurface ? Theme.SurfaceAlt : Theme.SurfaceHover;
        var baseColor = Primary ? Theme.Accent : Danger ? Theme.Mix(plain, Theme.Red, .18f) : plain;
        var hoverColor = Primary ? Theme.AccentHover : Danger ? Theme.Mix(plain, Theme.Red, .3f) : plainHover;
        var color = Enabled ? Theme.Mix(baseColor, hoverColor, hoverProgress) : Theme.Mix(Theme.Surface, Theme.Background, .45f);
        if (Enabled && pressed) color = ControlPaint.Dark(color, .12f);
        using var path = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), Math.Min(12, Height / 2));
        using var brush = new SolidBrush(color);
        e.Graphics.FillPath(brush, path);
        if (Dashed)
        {
            using var pen = new Pen(Theme.Accent, 1) { DashStyle = DashStyle.Dash };
            e.Graphics.DrawPath(pen, path);
        }
        if (Selected && !Primary)
        {
            using var bar = new SolidBrush(Theme.Accent);
            e.Graphics.FillRectangle(bar, 0, 8, 3, Height - 16);
        }
        var text = string.IsNullOrEmpty(Icon) ? Text : $"{Icon}   {Text}";
        var foreground = !Enabled ? Theme.Muted : Danger ? Theme.Red : Dashed && !Selected ? Theme.AccentHover : ForeColor;
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
            (TextAlign is ContentAlignment.MiddleLeft ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter);
        var bounds = TextAlign is ContentAlignment.MiddleLeft ? new Rectangle(14, 0, Width - 18, Height) : ClientRectangle;
        TextRenderer.DrawText(e.Graphics, text, Font, bounds, foreground, flags);
    }

    private Color SurfaceBehind()
    {
        for (var parent = Parent; parent is not null; parent = parent.Parent)
            if (parent.BackColor != Color.Transparent) return parent.BackColor;
        return Theme.Background;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public DialogResult DialogResult { get; set; }
    public void NotifyDefault(bool value) { }
    public void PerformClick() { if (Enabled && Visible) OnClick(EventArgs.Empty); }

    protected override void Dispose(bool disposing) { if (disposing) animation.Dispose(); base.Dispose(disposing); }

    internal static GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0 || bounds.Width <= 0 || bounds.Height <= 0) { path.AddRectangle(bounds); return path; }
        var d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal class SmoothPanel : Panel
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Radius { get; set; } = 14;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color? BorderColor { get; set; }

    public SmoothPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Theme.Surface;
        Padding = new Padding(20);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var outside = Parent?.BackColor ?? Theme.Background;
        e.Graphics.Clear(outside == Color.Transparent ? Theme.Background : outside);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = ModernButton.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), Radius);
        PaintSurface(e.Graphics, path);
        if (BorderColor is { } border)
        {
            using var pen = new Pen(border, 1.4f);
            e.Graphics.DrawPath(pen, path);
        }
    }

    protected virtual void PaintSurface(Graphics g, GraphicsPath path)
    {
        using var brush = new SolidBrush(BackColor);
        g.FillPath(brush, path);
    }
}

internal sealed class KpiTile : SmoothPanel
{
    private readonly Label title = Ui.Label("", 9, Theme.Muted, FontStyle.Bold);
    private readonly Label value = Ui.Label("—", 24, Theme.Text, FontStyle.Bold);
    private readonly Label note = Ui.Label("", 9, Theme.Muted);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Accent { get; set; }

    public KpiTile(string caption, float valueSize = 24)
    {
        Padding = new Padding(18, 14, 18, 12);
        Margin = new Padding(0, 0, 14, 0);
        title.Text = caption;
        value.Font = Theme.Font(valueSize, FontStyle.Bold);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, BackColor = Color.Transparent };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        layout.Controls.Add(title, 0, 0); layout.Controls.Add(value, 0, 1); layout.Controls.Add(note, 0, 2);
        Controls.Add(layout);
    }

    public void Set(string text, string? caption = null, Color? color = null)
    {
        value.Text = text;
        value.ForeColor = color ?? Theme.Text;
        note.Text = caption ?? "";
    }

    protected override void PaintSurface(Graphics g, GraphicsPath path)
    {
        if (!Accent) { base.PaintSurface(g, path); return; }
        using var brush = new LinearGradientBrush(ClientRectangle.IsEmpty ? new Rectangle(0, 0, 1, 1) : ClientRectangle,
            Theme.Mix(Theme.Surface, Theme.Accent, .42f), Theme.Mix(Theme.Surface, Theme.Violet, .25f), 35f);
        g.FillPath(brush, path);
        using var pen = new Pen(Color.FromArgb(110, Theme.Accent), 1.2f);
        g.DrawPath(pen, path);
    }
}

internal sealed class CardPanel : SmoothPanel
{
    private readonly Label title = Ui.Label("", 12, Theme.Text, FontStyle.Bold);
    private readonly Label hint = Ui.Label("", 9, Theme.Muted);
    public Panel Body { get; } = new() { Dock = DockStyle.Fill, BackColor = Theme.Surface };
    public FlowLayoutPanel Tools { get; } = new() { Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Surface };

    public CardPanel(string caption, string? subtitle = null)
    {
        Padding = new Padding(18, 12, 18, 16);
        title.Text = caption;
        title.AutoSize = true; title.Dock = DockStyle.Left;
        hint.Text = subtitle ?? ""; hint.Dock = DockStyle.Fill; hint.TextAlign = ContentAlignment.MiddleLeft; hint.Padding = new Padding(10, 3, 0, 0);
        var head = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Theme.Surface };
        head.Controls.Add(hint); head.Controls.Add(Tools); head.Controls.Add(title);
        Controls.Add(Body); Controls.Add(head);
    }

    public string Caption { get => title.Text; set => title.Text = value ?? ""; }
}

internal sealed class ModernComboBox : ComboBox
{
    public ModernComboBox()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        FlatStyle = FlatStyle.Flat;
        BackColor = Theme.SurfaceHover;
        ForeColor = Theme.Text;
        Font = Theme.Font(10.5f);
        DrawMode = DrawMode.OwnerDrawFixed;
        ItemHeight = Font.Height + 10;
        Margin = new Padding(0, 4, 10, 0);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        var selected = e.Index >= 0 && (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
        using var background = new SolidBrush(selected ? Theme.Accent : Theme.SurfaceHover);
        e.Graphics.FillRectangle(background, e.Bounds);
        if (e.Index < 0) return;
        var bounds = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 16), e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, bounds, Enabled ? Theme.Text : Theme.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

internal sealed class AutomationHint : SmoothPanel
{
    public AutomationHint(string text)
    {
        Dock = DockStyle.Top;
        Height = 46;
        Radius = 12;
        Padding = new Padding(16, 0, 16, 0);
        BackColor = Theme.Mix(Theme.Background, Theme.Accent, .14f);
        BorderColor = Color.FromArgb(120, Theme.Accent);
        var label = Ui.Label($"ⓘ  {char.ToUpper(text[0])}{text[1..]}", 9.5f, Theme.Text);
        label.AutoEllipsis = true;
        Controls.Add(label);
    }
}

internal sealed class ProgressLine : Control
{
    private double value;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double Value { get => value; set { this.value = Math.Clamp(value, 0, 100); Invalidate(); } }

    public ProgressLine()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Height = 10;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var track = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = ModernButton.Rounded(track, Height / 2))
        using (var brush = new SolidBrush(Theme.SurfaceAlt)) e.Graphics.FillPath(brush, path);
        var width = (int)Math.Round((Width - 1) * value / 100);
        if (width < Height) return;
        var fill = new Rectangle(0, 0, width, Height - 1);
        using var fillPath = ModernButton.Rounded(fill, Height / 2);
        using var gradient = new LinearGradientBrush(new Rectangle(0, 0, Math.Max(1, Width), Height), Theme.Accent, Theme.Green, 0f);
        e.Graphics.FillPath(gradient, fillPath);
    }
}

internal abstract class Page : UserControl
{
    private readonly Label title = Ui.Label("", 22, Theme.Text, FontStyle.Bold);
    private readonly Label subtitle = Ui.Label("", 9.5f, Theme.Muted);
    private bool busy;
    protected ApiClient Api { get; }
    protected FlowLayoutPanel Actions { get; } = new() { Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Background, Padding = new Padding(0, 14, 0, 0) };
    protected Panel Body { get; } = new() { Dock = DockStyle.Fill, BackColor = Theme.Background };
    internal MainForm? Shell => FindForm() as MainForm;

    protected Page(ApiClient api, string pageTitle, string pageSubtitle)
    {
        Api = api;
        Dock = DockStyle.Fill;
        BackColor = Theme.Background;
        DoubleBuffered = true;
        Padding = new Padding(30, 22, 30, 22);
        title.Text = pageTitle;
        subtitle.Text = pageSubtitle;
        var text = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, BackColor = Theme.Background };
        text.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        text.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        text.Controls.Add(title, 0, 0); text.Controls.Add(subtitle, 0, 1);
        var head = new Panel { Dock = DockStyle.Top, Height = 78, BackColor = Theme.Background };
        head.Controls.Add(text); head.Controls.Add(Actions);
        Controls.Add(Body); Controls.Add(head);
    }

    public string Title { get => title.Text; protected set => title.Text = value; }
    public string Subtitle { get => subtitle.Text; protected set => subtitle.Text = value; }

    public abstract Task LoadAsync();
    public virtual Task OnLeaveAsync() => Task.CompletedTask;

    protected async Task RunAsync(Func<Task> action, string? success = null)
    {
        if (busy) return;
        busy = true;
        UseWaitCursor = true;
        try
        {
            await action();
            if (success is not null) Ui.Toast(this, success);
        }
        catch (ApiException ex) { Ui.Toast(this, ex.Message, true); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException) { Ui.Toast(this, ex.Message, true); }
        finally
        {
            busy = false;
            if (!IsDisposed) UseWaitCursor = false;
        }
    }
}

internal static class Ui
{
    /// Ждёт фоновую задачу не дольше timeout и гасит её исключения: зависший драйвер камеры не должен блокировать окно.
    public static async Task<bool> SettleAsync(Task? task, int timeout = 3000)
    {
        if (task is null) return true;
        if (await Task.WhenAny(task, Task.Delay(timeout)) != task)
        {
            _ = task.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            return false;
        }
        try { await task; } catch { }
        return true;
    }

    public static Label Label(string text, float size, Color color, FontStyle style = FontStyle.Regular) => new()
    {
        Text = text, Font = Theme.Font(size, style), ForeColor = color, BackColor = Color.Transparent,
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false
    };

    public static TextBox Input(string placeholder) => new()
    {
        PlaceholderText = placeholder, BorderStyle = BorderStyle.FixedSingle, BackColor = Theme.SurfaceHover,
        ForeColor = Theme.Text, Font = Theme.Font(11), Margin = new Padding(0, 2, 0, 6)
    };

    public static FlowLayoutPanel Row() => new()
    {
        Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent, AutoSize = false
    };

    public static DataGridView Grid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, BackgroundColor = Theme.Surface, BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, GridColor = Theme.Line,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None, RowHeadersVisible = false,
            EnableHeadersVisualStyles = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false, ReadOnly = true, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            ColumnHeadersHeight = 40, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            Font = Theme.Font(10)
        };
        typeof(DataGridView).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(grid, true);
        grid.RowTemplate.Height = 40;
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Theme.Surface, ForeColor = Theme.Muted, Font = Theme.Font(9, FontStyle.Bold), SelectionBackColor = Theme.Surface, SelectionForeColor = Theme.Muted, Padding = new Padding(8, 0, 0, 0) };
        grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Theme.Surface, ForeColor = Theme.Text, SelectionBackColor = Theme.SurfaceHover, SelectionForeColor = Theme.Text, Padding = new Padding(8, 0, 0, 0) };
        grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Theme.Surface };
        return grid;
    }

    public static void FillRounded(Graphics g, Rectangle bounds, int radius, Color color)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = ModernButton.Rounded(bounds, radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    public static void Toast(Control host, string text, bool error = false)
    {
        var form = host.FindForm();
        if (form is null || form.IsDisposed) return;
        var toast = new ToastLabel(text, error);
        form.Controls.Add(toast);
        toast.Location = new Point(form.ClientSize.Width - toast.Width - 24, form.ClientSize.Height - toast.Height - 24 - 52 * form.Controls.OfType<ToastLabel>().Count(t => t != toast));
        toast.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        toast.BringToFront();
        var timer = new System.Windows.Forms.Timer { Interval = 3500 };
        timer.Tick += (_, _) => { timer.Dispose(); if (!toast.IsDisposed) { form.Controls.Remove(toast); toast.Dispose(); } };
        timer.Start();
    }

    public static bool Confirm(IWin32Window? owner, string title, string text, string ok = "Подтвердить", bool danger = false)
    {
        using var dialog = new DialogShell(title, 520, 230);
        var message = Label(text, 10.5f, Theme.Text);
        message.TextAlign = ContentAlignment.TopLeft;
        dialog.Content.Controls.Add(message);
        var confirm = dialog.AddButton(ok, DialogResult.OK, primary: !danger);
        confirm.Danger = danger;
        dialog.AddButton("Отмена", DialogResult.Cancel);
        return dialog.ShowDialog(owner) == DialogResult.OK;
    }

    public static string? Prompt(IWin32Window? owner, string title, string label, string initial = "")
    {
        using var dialog = new DialogShell(title, 520, 240);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, BackColor = Theme.Background };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        var input = Input(""); input.Text = initial; input.Dock = DockStyle.Fill;
        layout.Controls.Add(Label(label, 9.5f, Theme.Muted), 0, 0); layout.Controls.Add(input, 0, 1);
        dialog.Content.Controls.Add(layout);
        dialog.AddButton("Сохранить", DialogResult.OK, primary: true);
        dialog.AddButton("Отмена", DialogResult.Cancel);
        dialog.Shown += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog(owner) == DialogResult.OK ? input.Text.Trim() : null;
    }

    public static void Message(IWin32Window? owner, string title, string text)
    {
        using var dialog = new DialogShell(title, 620, 420);
        var box = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Theme.Surface, ForeColor = Theme.Text, Font = Theme.Font(10), Text = text };
        dialog.Content.Controls.Add(box);
        dialog.AddButton("Закрыть", DialogResult.OK, primary: true);
        dialog.ShowDialog(owner);
    }

    private sealed class ToastLabel : Control
    {
        private readonly bool error;

        public ToastLabel(string text, bool error)
        {
            this.error = error;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Text = (error ? "⚠  " : "✓  ") + text;
            Font = Theme.Font(10, FontStyle.Bold);
            var size = TextRenderer.MeasureText(Text, Font);
            Size = new Size(Math.Min(560, size.Width + 36), 44);
            BackColor = Theme.Background;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            FillRounded(e.Graphics, new Rectangle(0, 0, Width - 1, Height - 1), 12, error ? Theme.Mix(Theme.SurfaceHover, Theme.Red, .25f) : Theme.SurfaceAlt);
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(16, 0, Width - 24, Height), error ? Theme.Red : Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }
}

internal class DialogShell : DarkForm
{
    private readonly FlowLayoutPanel buttons = new() { Dock = DockStyle.Bottom, Height = 58, FlowDirection = FlowDirection.RightToLeft, BackColor = Theme.Background, Padding = new Padding(16, 8, 6, 8) };
    public Panel Content { get; } = new() { Dock = DockStyle.Fill, BackColor = Theme.Background, Padding = new Padding(24, 8, 24, 8) };

    public DialogShell(string title, int width, int height)
    {
        Text = title;
        ClientSize = new Size(width, height);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        var heading = Ui.Label(title, 15, Theme.Text, FontStyle.Bold);
        heading.Dock = DockStyle.Top; heading.Height = 56; heading.Padding = new Padding(24, 10, 0, 0);
        Controls.Add(Content); Controls.Add(buttons); Controls.Add(heading);
    }

    public ModernButton AddButton(string text, DialogResult result, bool primary = false)
    {
        var button = new ModernButton { Text = text, Primary = primary, Width = Math.Max(120, TextRenderer.MeasureText(text, Theme.Font(10, FontStyle.Bold)).Width + 40), Height = 40 };
        button.Click += (_, _) => { DialogResult = result; Close(); };
        buttons.Controls.Add(button);
        if (primary) AcceptButton = button;
        return button;
    }

    public FlowLayoutPanel Buttons => buttons;

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return true; }
        return base.ProcessDialogKey(keyData);
    }
}
