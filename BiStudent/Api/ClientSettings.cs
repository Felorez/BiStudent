using System.Text;
using System.Text.Json;

namespace BiStudent;

internal sealed class ClientSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BiStudent", "client.json");

    public string ServerUrl { get; set; } = "http://localhost:5080";
    public string LastLogin { get; set; } = "";
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

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions), Encoding.UTF8);
    }
}
