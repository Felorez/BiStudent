using BiStudent.Contracts;

namespace BiStudent;

internal sealed class ExcuseDialog : IDisposable
{
    private readonly DialogShell shell = new("Уважительная причина", 540, 530);
    private readonly RadioButton[] reasons = new RadioButton[4];
    private readonly CheckBox document = new() { Text = "Подтверждающий документ предоставлен (справка, заявление, приказ)", AutoSize = true, ForeColor = Theme.Text, BackColor = Theme.Background };
    private readonly TextBox comment = Ui.Input("Комментарий");
    private readonly Label counter = Ui.Label("", 9, Theme.Muted);

    public ExcuseDialog(string studentName, ExcuseReason? reason = null, bool hasDocument = false, string? initialComment = null)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 10, BackColor = Theme.Background };
        foreach (var height in new[] { 30, 24, 35, 35, 35, 35, 45, 24 })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        var name = Ui.Label(studentName, 10, Theme.Muted);
        name.AutoEllipsis = true;
        layout.Controls.Add(name, 0, 0);
        layout.Controls.Add(Ui.Label("Причина", 9, Theme.Muted), 0, 1);
        var values = Enum.GetValues<ExcuseReason>();
        for (var i = 0; i < values.Length; i++)
        {
            reasons[i] = new RadioButton
            {
                Text = UiFormat.ExcuseReasonText(values[i]), AutoSize = true,
                ForeColor = Theme.Text, BackColor = Theme.Background, Checked = values[i] == (reason ?? ExcuseReason.Illness)
            };
            layout.Controls.Add(reasons[i], 0, i + 2);
        }
        document.Checked = hasDocument;
        layout.Controls.Add(document, 0, 6);
        layout.Controls.Add(Ui.Label("Комментарий", 9, Theme.Muted), 0, 7);
        comment.Multiline = true;
        comment.ScrollBars = ScrollBars.Vertical;
        comment.MaxLength = 500;
        comment.Text = initialComment ?? "";
        comment.Dock = DockStyle.Fill;
        layout.Controls.Add(comment, 0, 8);
        counter.Dock = DockStyle.Right;
        counter.Width = 70;
        counter.TextAlign = ContentAlignment.MiddleRight;
        layout.Controls.Add(counter, 0, 9);
        comment.TextChanged += (_, _) => counter.Text = $"{comment.TextLength}/500";
        counter.Text = $"{comment.TextLength}/500";
        shell.Content.Controls.Add(layout);
        shell.AddButton("Сохранить", DialogResult.OK, primary: true);
        shell.AddButton("Отмена", DialogResult.Cancel);
    }

    public (ExcuseReason Reason, bool HasDocument, string? Comment) Result =>
        ((ExcuseReason)Array.FindIndex(reasons, item => item.Checked), document.Checked,
            string.IsNullOrWhiteSpace(comment.Text) ? null : comment.Text.Trim());

    public DialogResult ShowDialog(IWin32Window? owner) => shell.ShowDialog(owner);
    public void Dispose() => shell.Dispose();
}
