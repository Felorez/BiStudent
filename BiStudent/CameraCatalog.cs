using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text.Json;

namespace BiStudent;

internal sealed record CameraChoice(int Index, string Name, string DeviceId) { public override string ToString() => Name; }
internal static class CameraCatalog
{
    [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICreateDevEnum { [PreserveSig] int CreateClassEnumerator(ref Guid category, out IEnumMoniker? enumerator, int flags); }
    [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyBag {
        [PreserveSig] int Read([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Struct)] out object value, IntPtr errorLog);
        [PreserveSig] int Write([MarshalAs(UnmanagedType.LPWStr)] string name, ref object value);
    }
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"StudentAttendance","camera.json");
    public static List<CameraChoice> List()
    {
        var result = new List<CameraChoice>(); object? deviceEnum = null; IEnumMoniker? iterator = null;
        try {
            deviceEnum = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("62BE5D10-60EB-11D0-BD3B-00A0C911CE86"))!);
            var category = new Guid("860BB310-5D01-11D0-BD3B-00A0C911CE86");
            if (((ICreateDevEnum)deviceEnum!).CreateClassEnumerator(ref category,out iterator,0) != 0 || iterator is null) return result;
            var monikers = new IMoniker[1]; var index = 0;
            while (iterator.Next(1,monikers,IntPtr.Zero) == 0) {
                var moniker = monikers[0]; object? bag = null;
                try {
                    var guid = typeof(IPropertyBag).GUID; moniker.BindToStorage(null!,null!,ref guid,out bag);
                    var properties = (IPropertyBag)bag; properties.Read("FriendlyName",out var name,IntPtr.Zero);
                    properties.Read("DevicePath",out var path,IntPtr.Zero);
                    moniker.GetDisplayName(null!,null!,out var display);
                    result.Add(new CameraChoice(index, name?.ToString() ?? $"Камера {index+1}",path?.ToString() ?? display));
                } catch { result.Add(new CameraChoice(index,$"Камера {index+1}","index:" + index)); }
                finally { if (bag is not null) Marshal.ReleaseComObject(bag); Marshal.ReleaseComObject(moniker); index++; }
            }
        } catch { if (result.Count == 0) result.Add(new CameraChoice(0,"Камера по умолчанию","index:0")); }
        finally { if (iterator is not null) Marshal.ReleaseComObject(iterator); if (deviceEnum is not null) Marshal.ReleaseComObject(deviceEnum); }
        return result;
    }
    public static CameraChoice? Preferred(List<CameraChoice> choices)
    {
        try { var id = JsonSerializer.Deserialize<string>(File.ReadAllText(FilePath)); return choices.FirstOrDefault(x => x.DeviceId == id) ?? choices.FirstOrDefault(); }
        catch { return choices.FirstOrDefault(); }
    }
    public static void Save(CameraChoice choice) { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); File.WriteAllText(FilePath,JsonSerializer.Serialize(choice.DeviceId)); }
}

internal sealed class CameraSelectionForm : Form
{
    public CameraChoice? Selected { get; private set; }
    public CameraSelectionForm()
    {
        FormStyle.Dialog(this,"Выбор камеры",new Size(590,260));
        var layout = new TableLayoutPanel { Dock=DockStyle.Fill, Padding=new Padding(24),RowCount=3 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,50)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,46)); layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        var caption = FormStyle.Caption("Подключённые веб-камеры Windows"); layout.Controls.Add(caption,0,0);
        var box = new ComboBox { Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDownList,FlatStyle=FlatStyle.Flat,BackColor=Theme.Surface,ForeColor=Theme.Text,Font=Theme.Font(11),DrawMode=DrawMode.OwnerDrawFixed,ItemHeight=27 };
        box.DrawItem += (_,e) => { using var brush=new SolidBrush((e.State & DrawItemState.Selected)!=0 ? Theme.SurfaceHover : Theme.Surface); e.Graphics.FillRectangle(brush,e.Bounds); if (e.Index>=0) TextRenderer.DrawText(e.Graphics,box.Items[e.Index]?.ToString() ?? "",box.Font,e.Bounds,Theme.Text,TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis); };
        var cameras = CameraCatalog.List(); box.Items.AddRange(cameras.Cast<object>().ToArray()); box.SelectedItem=CameraCatalog.Preferred(cameras); layout.Controls.Add(box,0,1);
        var save = new ModernButton { Text="Использовать камеру",Primary=true,Width=220,Enabled=cameras.Count>0 };
        if (cameras.Count == 0) caption.Text="Камеры не найдены. Подключите камеру и повторите.";
        save.Click += (_,_) => { if (box.SelectedItem is not CameraChoice camera) return; Selected=camera; CameraCatalog.Save(camera); DialogResult=DialogResult.OK; Close(); };
        layout.Controls.Add(save,0,2); Controls.Add(layout);
    }
}
