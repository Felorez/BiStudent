using System.Security.Cryptography;
using System.Text.Json;
using BiStudent.Server.Services;
using Microsoft.AspNetCore.DataProtection;

namespace BiStudent.Server.Firebase;

public sealed class FirebaseSessionStore(
    FirebaseAuthClient auth, IDataProtectionProvider protection, StoragePaths paths,
    TimeProvider? clock = null)
{
    private sealed record StoredSession(string Uid, string Email, string RefreshToken, int TeacherId);

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly IDataProtector protector =
        protection.CreateProtector("BiStudent.FirebaseSession");
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private FirebaseSession? session;
    private int? teacherId;

    private string FilePath => Path.Combine(paths.DataDirectory, "firebase.session");

    public int? TeacherId
    {
        get
        {
            gate.Wait();
            try { return teacherId; }
            finally { gate.Release(); }
        }
    }

    public string? Uid
    {
        get
        {
            gate.Wait();
            try
            {
                if (session is not null) return session.Uid;
                if (!File.Exists(FilePath)) return null;
                try
                {
                    return JsonSerializer.Deserialize<StoredSession>(
                        protector.Unprotect(File.ReadAllBytes(FilePath)))?.Uid;
                }
                catch (Exception exception) when (exception is CryptographicException or
                    JsonException or IOException) { return null; }
            }
            finally { gate.Release(); }
        }
    }

    public void Set(FirebaseSession value, int localTeacherId)
    {
        gate.Wait();
        try { Save(value, localTeacherId); }
        finally { gate.Release(); }
    }

    public void Clear()
    {
        gate.Wait();
        try { ClearStored(); }
        finally { gate.Release(); }
    }

    public async Task<FirebaseSession?> GetAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (session is null && File.Exists(FilePath))
            {
                try
                {
                    var bytes = await File.ReadAllBytesAsync(FilePath, cancellationToken);
                    var stored = JsonSerializer.Deserialize<StoredSession>(protector.Unprotect(bytes))
                        ?? throw new JsonException();
                    teacherId = stored.TeacherId;
                    session = new FirebaseSession(stored.Uid, stored.Email, "",
                        stored.RefreshToken, DateTimeOffset.MinValue);
                }
                catch (Exception exception) when (exception is CryptographicException or
                    JsonException or FormatException)
                {
                    ClearStored();
                    return null;
                }
            }

            if (session is null || !session.ExpiresSoon(time)) return session;
            try
            {
                var refreshed = await auth.RefreshAsync(
                    session.RefreshToken, session.Email, cancellationToken);
                Save(refreshed, teacherId!.Value);
                return refreshed;
            }
            catch (FirebaseAuthException exception) when (exception.Code is
                "TOKEN_EXPIRED" or "INVALID_REFRESH_TOKEN" or "USER_DISABLED" or "USER_NOT_FOUND")
            {
                ClearStored();
                return null;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private void Save(FirebaseSession value, int localTeacherId)
    {
        var stored = new StoredSession(value.Uid, value.Email, value.RefreshToken, localTeacherId);
        var bytes = protector.Protect(JsonSerializer.SerializeToUtf8Bytes(stored));
        Directory.CreateDirectory(paths.DataDirectory);
        var temporary = FilePath + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, FilePath, overwrite: true);
        session = value;
        teacherId = localTeacherId;
    }

    private void ClearStored()
    {
        session = null;
        teacherId = null;
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }
}
