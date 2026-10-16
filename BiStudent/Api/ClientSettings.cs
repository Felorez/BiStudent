using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BiStudent;

internal sealed class ClientSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BiStudent", "client.json");
    private static readonly byte[] TokenEntropy = Encoding.UTF8.GetBytes("BiStudent.Refresh");

    public string ServerUrl { get; set; } = "http://localhost:5080";
    public string LastLogin { get; set; } = "";
    public bool RememberMe { get; set; }
    public string? ProtectedRefreshToken { get; set; }
    public string? RefreshTokenServer { get; set; }
    public string? CameraId { get; set; }
    public string? CameraName { get; set; }
    public int? CameraIndex { get; set; }

    public static ClientSettings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(FilePath, Encoding.UTF8), JsonOptions)
                ?? new ClientSettings();
            if (string.IsNullOrWhiteSpace(settings.ServerUrl)) settings.ServerUrl = "http://localhost:5080";
            settings.LastLogin ??= "";
            return settings;
        }
        catch { return new ClientSettings(); }
    }

    public static bool SameServer(string? left, string? right) =>
        string.Equals(left?.Trim().TrimEnd('/'), right?.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    public string? LoadRefreshToken()
    {
        if (string.IsNullOrEmpty(ProtectedRefreshToken) || string.IsNullOrWhiteSpace(RefreshTokenServer)
            || !SameServer(RefreshTokenServer, ServerUrl)) return null;
        try
        {
            var bytes = Convert.FromBase64String(ProtectedRefreshToken);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, TokenEntropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }

    public void StoreRefreshToken(string? token)
    {
        RefreshTokenServer = token is null ? null : ServerUrl;
        ProtectedRefreshToken = token is null ? null : Convert.ToBase64String(
            ProtectedData.Protect(Encoding.UTF8.GetBytes(token), TokenEntropy, DataProtectionScope.CurrentUser));
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions), Encoding.UTF8);
    }
}
