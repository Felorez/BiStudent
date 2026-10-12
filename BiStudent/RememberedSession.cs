using System.Security.Cryptography;
using System.Text.Json;

namespace BiStudent;

internal sealed record DeviceSession(string ProjectId, string Uid, string RefreshToken);
internal static class RememberedSession
{
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StudentAttendance", "teacher-session.bin");
    public static void Save(FirebaseSettings settings, TeacherSession session, string? path = null)
    {
        if (string.IsNullOrEmpty(session.RefreshToken)) return;
        var file=path ?? FilePath; Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new DeviceSession(settings.ProjectId,session.Uid,session.RefreshToken));
        File.WriteAllBytes(file + ".tmp",ProtectedData.Protect(bytes,null,DataProtectionScope.CurrentUser));
        File.Move(file + ".tmp",file,true);
    }
    public static string? Load(FirebaseSettings settings,string? path = null)
    {
        try {
            var data = JsonSerializer.Deserialize<DeviceSession>(ProtectedData.Unprotect(File.ReadAllBytes(path ?? FilePath),null,DataProtectionScope.CurrentUser));
            return data?.ProjectId == settings.ProjectId && data.Uid == settings.TeacherUid ? data.RefreshToken : null;
        } catch { return null; }
    }
    public static void Forget(string? path = null) { var file=path ?? FilePath; if (File.Exists(file)) File.Delete(file); }
    public static bool Exists => File.Exists(FilePath);
}
