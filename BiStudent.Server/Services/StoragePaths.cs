namespace BiStudent.Server.Services;

public sealed record StoragePaths(string DatabasePath, string ConnectionString)
{
    public string DataDirectory => Path.GetDirectoryName(Path.GetFullPath(DatabasePath))!;
    public string KeysDirectory => Path.Combine(DataDirectory, "keys");
    public string BackupsDirectory => Path.Combine(DataDirectory, "backups");
}
