namespace BiStudent;

internal sealed class FaceInviteForm : Form
{
    public FaceInviteForm(string name,string code)
    {
        FormStyle.Dialog(this,"Регистрация лица на сайте",new Size(660,320));
        var layout=new TableLayoutPanel { Dock=DockStyle.Fill,Padding=new Padding(24),RowCount=4 };
        foreach (var height in new[] { 50,50,80,48 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute,height));
        layout.Controls.Add(FormStyle.Caption(name),0,0);
        var value=FormStyle.Input(""); value.Text=code; value.ReadOnly=true; layout.Controls.Add(value,0,1);
        layout.Controls.Add(FormStyle.Caption("Передайте этот код только выбранному студенту.\nНа сайте: «Зарегистрировать лицо». Код действует 24 часа и принимает один набор снимков."),0,2);
        var copy=new ModernButton { Text="Скопировать код",Primary=true,Width=220 };
        copy.Click += (_,_) => { Clipboard.SetText(code); copy.Text="Скопировано"; }; layout.Controls.Add(copy,0,3); Controls.Add(layout);
    }
}
