using OpenCvSharp;

namespace BiStudent;

internal static class CameraDevice
{
    // DirectShow avoids the slow Media Foundation negotiation on many Windows laptops.
    public static VideoCapture Open(CancellationToken token)
    {
        var choices = CameraCatalog.List(); var selected = CameraCatalog.Preferred(choices);
        if (selected is null) throw new InvalidOperationException("Камеры не найдены. Подключите веб-камеру.");
        // Enumeration is DirectShow: never silently open a different MSMF index.
        foreach (var api in new[] { VideoCaptureAPIs.DSHOW })
        {
            token.ThrowIfCancellationRequested();
            var camera = new VideoCapture();
            try
            {
                if (camera.Open(selected.Index, api))
                {
                    // Keep detail for faces across the classroom. Unsupported modes
                    // are negotiated by DirectShow without probing/opening again.
                    camera.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('M', 'J', 'P', 'G'));
                    camera.Set(VideoCaptureProperties.FrameWidth, 1280);
                    camera.Set(VideoCaptureProperties.FrameHeight, 720);
                    token.ThrowIfCancellationRequested();
                    return camera;
                }
            }
            catch { camera.Dispose(); throw; }
            camera.Dispose();
        }
        throw new InvalidOperationException("Камера не найдена или занята. Закройте другие приложения с камерой.");
    }
}

internal sealed class CameraLoader : Control
{
    private readonly System.Windows.Forms.Timer animation = new() { Interval = 30 };
    private int angle;
    public CameraLoader()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true; Dock = DockStyle.Fill; BackColor = Theme.Sidebar; ForeColor = Theme.Text;
        Font = Theme.Font(11);
        Text = "Подключение камеры…";
        animation.Tick += (_, _) => { angle = (angle + 9) % 360; Invalidate(); };
        animation.Start();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var pen = new Pen(Theme.Accent, 4);
        e.Graphics.DrawArc(pen, Width / 2 - 18, Height / 2 - 42, 36, 36, angle, 260);
        TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(12, Height / 2 + 6, Width - 24, 58), ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
    }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (Visible) animation.Start(); else animation.Stop(); }
    protected override void Dispose(bool disposing) { if (disposing) animation.Dispose(); base.Dispose(disposing); }
}
