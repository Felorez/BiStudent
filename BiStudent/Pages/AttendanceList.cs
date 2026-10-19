using System.Drawing.Drawing2D;
using BiStudent.Contracts;

namespace BiStudent;

internal sealed class AttendanceList : Panel
{
    private const int RowHeight = 60;
    private static readonly (AttendanceStatus Status, string Short, string Long)[] Buttons =
    [
        (AttendanceStatus.Present, "П", "Был"), (AttendanceStatus.Absent, "Н", "Нет"),
        (AttendanceStatus.Excused, "Ув.", "Уважит.")
    ];

    private readonly Canvas canvas;
    private readonly Dictionary<int, DateTime> flashes = [];
    private readonly System.Windows.Forms.Timer flashTimer = new() { Interval = 60 };
    private List<AttendanceRowDto> rows = [];
    private (int Row, int Button) hover = (-1, -1);

    public AttendanceList(bool wideButtons = false)
    {
        WideButtons = wideButtons;
        AutoScroll = true;
        BackColor = Theme.Surface;
        DoubleBuffered = true;
        canvas = new Canvas(this) { Location = Point.Empty };
        Controls.Add(canvas);
        flashTimer.Tick += (_, _) =>
        {
            foreach (var id in flashes.Where(f => (DateTime.Now - f.Value).TotalSeconds > 1.8).Select(f => f.Key).ToList()) flashes.Remove(id);
            if (flashes.Count == 0) flashTimer.Stop();
            canvas.Invalidate();
        };
        Resize += (_, _) => Relayout();
    }

    public bool WideButtons { get; }
    public string EmptyText { get; set; } = "Список пуст";
    public event Func<AttendanceRowDto, AttendanceStatus?, Task>? StatusChosen;
    public event Func<AttendanceRowDto, Task>? GradeChosen;
    public IReadOnlyList<AttendanceRowDto> Rows => rows;

    public void SetRows(List<AttendanceRowDto> value)
    {
        rows = value;
        Relayout();
        canvas.Invalidate();
    }

    public void Flash(int studentId)
    {
        flashes[studentId] = DateTime.Now;
        flashTimer.Start();
    }

    private void Relayout()
    {
        var vertical = rows.Count * RowHeight + 4 > ClientSize.Height;
        canvas.Width = Math.Max(10, ClientSize.Width - (vertical && !VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0) - 1);
        canvas.Height = Math.Max(ClientSize.Height, rows.Count * RowHeight + 4);
    }

    private int ButtonWidth => WideButtons ? 78 : 36;

    private Rectangle GradeBounds(int row) =>
        new(canvas.Width - 86, row * RowHeight + (RowHeight - 32) / 2, 74, 32);

    private Rectangle ButtonBounds(int row, int index)
    {
        var width = ButtonWidth;
        var x = canvas.Width - 94 - (Buttons.Length - index) * (width + 4);
        return new Rectangle(x, row * RowHeight + (RowHeight - 32) / 2, width, 32);
    }

    private (int Row, int Button) HitTest(Point point)
    {
        var row = point.Y / RowHeight;
        if (row < 0 || row >= rows.Count) return (-1, -1);
        if (GradeBounds(row).Contains(point)) return (row, -2);
        for (var i = 0; i < Buttons.Length; i++)
            if (ButtonBounds(row, i).Contains(point)) return (row, i);
        return (row, -1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) flashTimer.Dispose();
        base.Dispose(disposing);
    }

    private sealed class Canvas : Control
    {
        private readonly AttendanceList owner;

        public Canvas(AttendanceList owner)
        {
            this.owner = owner;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var hit = owner.HitTest(e.Location);
            if (hit != owner.hover) { owner.hover = hit; Invalidate(); }
            Cursor = hit.Button != -1 ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { owner.hover = (-1, -1); Invalidate(); base.OnMouseLeave(e); }

        protected override async void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            var (row, button) = owner.HitTest(e.Location);
            if (row >= 0 && button == -2)
            {
                if (owner.GradeChosen is not null) await owner.GradeChosen(owner.rows[row]);
                return;
            }
            if (row < 0 || button < 0 || owner.StatusChosen is null) return;
            var item = owner.rows[row];
            var status = Buttons[button].Status;
            await owner.StatusChosen(item, item.Status == status ? null : status);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (owner.rows.Count == 0)
            {
                TextRenderer.DrawText(g, owner.EmptyText, Theme.Font(10), new Rectangle(0, 20, Width, 30), Theme.Muted, TextFormatFlags.HorizontalCenter);
                return;
            }
            var first = Math.Max(0, e.ClipRectangle.Top / RowHeight);
            var last = Math.Min(owner.rows.Count - 1, e.ClipRectangle.Bottom / RowHeight);
            for (var i = first; i <= last; i++)
            {
                var row = owner.rows[i];
                var bounds = new Rectangle(0, i * RowHeight, Width, RowHeight);
                if (owner.flashes.TryGetValue(row.StudentId, out var started))
                {
                    var alpha = (int)Math.Clamp(110 * (1 - (DateTime.Now - started).TotalSeconds / 1.8), 0, 110);
                    Ui.FillRounded(g, Rectangle.Inflate(bounds, -2, -3), 10, Color.FromArgb(alpha, Theme.Green));
                }
                else if (owner.hover is var (hoverRow, _) && hoverRow == i) Ui.FillRounded(g, Rectangle.Inflate(bounds, -2, -3), 10, Theme.SurfaceHover);
                var textWidth = owner.ButtonBounds(i, 0).Left - 18;
                TextRenderer.DrawText(g, row.FullName, Theme.Font(10, FontStyle.Bold), new Rectangle(12, bounds.Top + 7, textWidth, 22), Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                var info = row.Status is null ? "ждём отметки"
                    : row.Status == AttendanceStatus.Excused
                        ? $"{UiFormat.ExcuseReasonText(row.Reason ?? ExcuseReason.Other)}{(row.HasDocument ? " · документ" : "")}"
                        : $"{UiFormat.StatusText(row.Status)} · {UiFormat.MethodText(row.Method)}{(row.MarkedAt is { } at ? $" · {at:HH:mm}" : "")}";
                TextRenderer.DrawText(g, info, Theme.Font(8.5f), new Rectangle(12, bounds.Top + 29, textWidth, 18), row.Status is null ? Theme.Muted : UiFormat.StatusColor(row.Status), TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                var grade = owner.GradeBounds(i);
                Ui.FillRounded(g, grade, 9, owner.hover == (i, -2) ? Theme.SurfaceAlt : Theme.SurfaceHover);
                TextRenderer.DrawText(g, row.Score?.ToString() ?? "Балл", Theme.Font(9.5f, FontStyle.Bold), grade, row.Score.HasValue ? Theme.Text : Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                for (var b = 0; b < Buttons.Length; b++)
                {
                    var rect = owner.ButtonBounds(i, b);
                    var status = Buttons[b].Status;
                    var active = row.Status == status;
                    var color = UiFormat.StatusColor(status);
                    var hovered = owner.hover;
                    var fill = active ? color : hovered == (i, b) ? Theme.SurfaceAlt : Theme.SurfaceHover;
                    Ui.FillRounded(g, rect, 9, fill);
                    TextRenderer.DrawText(g, owner.WideButtons ? Buttons[b].Long : Buttons[b].Short, Theme.Font(9.5f, FontStyle.Bold), rect,
                        active ? Theme.Background : color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }
                using var line = new Pen(Theme.Line);
                g.DrawLine(line, 8, bounds.Bottom - 1, Width - 8, bounds.Bottom - 1);
            }
        }
    }
}
