using System.Security.Claims;
using System.Security.Cryptography;

namespace BiStudent.Server.Services;

/// Случайный идентификатор поколения сессий: хранится вне базы и меняется при восстановлении копии,
/// чтобы токены, выданные раньше (в том числе другим сервером с теми же ключами), перестали действовать.
public sealed class SessionGeneration(StoragePaths paths)
{
    public const string ClaimType = "bistudent:session";
    private readonly object gate = new();
    private string? current;

    private string FilePath => Path.Combine(paths.DataDirectory, "session.generation");

    public string Current
    {
        get
        {
            lock (gate)
            {
                if (current is not null) return current;
                var stored = File.Exists(FilePath) ? File.ReadAllText(FilePath).Trim() : "";
                if (stored.Length < 32) { stored = NewId(); Write(stored); }
                return current = stored;
            }
        }
    }

    /// Сохраняет новое поколение до изменения данных; при ошибке записи бросает исключение и поколение не меняется.
    public void Rotate()
    {
        lock (gate)
        {
            var next = NewId();
            Write(next);
            current = next;
        }
    }

    public Claim Claim() => new(ClaimType, Current);

    public bool IsCurrent(ClaimsPrincipal user) => user.FindFirst(ClaimType)?.Value == Current;

    private static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    private void Write(string value)
    {
        Directory.CreateDirectory(paths.DataDirectory);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, value);
        File.Move(temporary, FilePath, overwrite: true);
    }
}
