using System.Drawing.Drawing2D;
using System.ComponentModel;

namespace BiStudent;

internal static class Theme
{
    public static readonly Color Background = ColorTranslator.FromHtml("#111827");
    public static readonly Color Sidebar = ColorTranslator.FromHtml("#0B1220");
    public static readonly Color Surface = ColorTranslator.FromHtml("#1D2939");
    public static readonly Color SurfaceHover = ColorTranslator.FromHtml("#273548");
    public static readonly Color Accent = ColorTranslator.FromHtml("#5B8DEF");
    public static readonly Color AccentHover = ColorTranslator.FromHtml("#75A2FA");
    public static readonly Color Text = ColorTranslator.FromHtml("#F4F7FB");
    public static readonly Color Muted = ColorTranslator.FromHtml("#9AAAC0");
    public static readonly Color Line = ColorTranslator.FromHtml("#334155");
    public static readonly Color Green = ColorTranslator.FromHtml("#36CFA5");
    public static readonly Color Red = ColorTranslator.FromHtml("#F27D86");
    public static Font Font(float size, FontStyle style = FontStyle.Regular) => new("Segoe UI", size, style);
}

internal sealed class ModernButton : Control
{
    private bool hovered;
    private bool pressed;
    private bool selected;
    private float hoverProgress;
    private readonly System.Windows.Forms.Timer animation;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Primary { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected { get => selected; set { selected = value; hoverProgress = value ? 1f : 0f; Invalidate(); } }

    public ModernButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true;
        Size = new Size(148, 42);
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

    protected override void OnMouseEnter(EventArgs e) { hovered = true; animation.Start(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; pressed = false; animation.Start(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var baseColor = Primary ? Theme.Accent : Theme.Surface;
        var hoverColor = Primary ? Theme.AccentHover : Theme.SurfaceHover;
        var color = Mix(baseColor, hoverColor, hoverProgress);
        if (pressed) color = ControlPaint.Dark(color, .12f);
        using var path = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 12);
        using var brush = new SolidBrush(color);
        e.Graphics.FillPath(brush, path);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private static Color Mix(Color a, Color b, float amount) => Color.FromArgb((int)(a.R + (b.R - a.R) * amount), (int)(a.G + (b.G - a.G) * amount), (int)(a.B + (b.B - a.B) * amount));
    protected override void Dispose(bool disposing) { if (disposing) animation.Dispose(); base.Dispose(disposing); }

    internal static GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class SmoothPanel : Panel
{
    public SmoothPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        BackColor = Theme.Surface;
        Padding = new Padding(20);
    }
}
