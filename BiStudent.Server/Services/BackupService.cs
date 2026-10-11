using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using BiStudent.Contracts;
using BiStudent.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BiStudent.Server.Services;

public sealed class BackupService(StoragePaths paths, IServiceScopeFactory scopes, RecognitionService recognition, SessionGeneration sessions, ILogger<BackupService> logger)
{
    private static readonly Regex BackupName = new(@"\Abistudent-[0-9]{8}-[0-9]{6}(-auto)?(-[0-9]+)?\.zip\z", RegexOptions.CultureInvariant);
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly Regex KeyName = new(@"\Akeys/key-[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\.xml\z", RegexOptions.CultureInvariant);
    private const long MaxZipSize = 200L * 1024 * 1024;
    private const long MaxDatabaseSize = 1024L * 1024 * 1024;
    private const long MaxManifestSize = 64 * 1024;
    private const long MaxKeySize = 64 * 1024;
    private const int MaxEntries = 64;
    private int maintenance;
    private int inflight;

    public async Task<BackupFileDto> CreateAsync(bool automatic, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { return await CreateLockedAsync(automatic, ct); }
        finally { gate.Release(); }
    }

    private async Task<BackupFileDto> CreateLockedAsync(bool automatic, CancellationToken ct)
    {
        Directory.CreateDirectory(paths.BackupsDirectory);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var stem = $"bistudent-{stamp}{(automatic ? "-auto" : "")}";
        var name = $"{stem}.zip";
        for (var suffix = 2; File.Exists(Path.Combine(paths.BackupsDirectory, name)); suffix++) name = $"{stem}-{suffix}.zip";
        var target = Path.Combine(paths.BackupsDirectory, name);
        var snapshot = Path.Combine(paths.BackupsDirectory, $"{Guid.NewGuid():N}.db.tmp");
        var temporary = target + ".tmp";
        try
        {
            using (var source = new SqliteConnection(paths.ConnectionString))
            {
                await source.OpenAsync(ct);
                using var command = source.CreateCommand();
                command.CommandText = $"VACUUM INTO '{snapshot.Replace("'", "''")}'";
                await command.ExecuteNonQueryAsync(ct);
            }
            var (students, marks) = await CountsAsync(snapshot, ct);
            var keyFiles = Directory.Exists(paths.KeysDirectory)
                ? Directory.EnumerateFiles(paths.KeysDirectory, "*.xml").Where(key => KeyName.IsMatch($"keys/{Path.GetFileName(key)}")).ToList()
                : [];
            if (new FileInfo(snapshot).Length > MaxDatabaseSize || keyFiles.Count + 2 > MaxEntries ||
                keyFiles.Any(key => new FileInfo(key).Length > MaxKeySize))
                throw new InvalidOperationException("Данные не помещаются в формат резервной копии (база больше 1 ГБ или слишком много ключей).");
            using (var stream = File.Create(temporary))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(snapshot, "bistudent.db");
                foreach (var key in keyFiles)
                    archive.CreateEntryFromFile(key, $"keys/{Path.GetFileName(key)}");
                using var manifest = archive.CreateEntry("manifest.json").Open();
                await JsonSerializer.SerializeAsync(manifest, new { app = "BiStudent", format = 1,
                    createdAt = DateTime.UtcNow.ToString("O"), students, marks }, cancellationToken: ct);
            }
            if (new FileInfo(temporary).Length > MaxZipSize)
                throw new InvalidOperationException("Копия получилась больше 200 МБ — её нельзя будет восстановить через приложение.");
            File.Move(temporary, target);
            var info = new FileInfo(target);
            logger.LogInformation("Создана резервная копия {Name}.", name);
            return new BackupFileDto(name, info.LastWriteTime, info.Length, automatic);
        }
        finally
        {
            TryDelete(snapshot);
            TryDelete(temporary);
        }
    }

    private void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning(ex, "Не удалось удалить временный файл {Path}.", path); }
    }

    public IReadOnlyList<BackupFileDto> List()
    {
        if (!Directory.Exists(paths.BackupsDirectory)) return [];
        var result = new List<BackupFileDto>();
        foreach (var path in Directory.EnumerateFiles(paths.BackupsDirectory, "bistudent-*.zip"))
        {
            try
            {
                var info = new FileInfo(path);
                if (BackupName.IsMatch(info.Name))
                    result.Add(new BackupFileDto(info.Name, info.LastWriteTime, info.Length, info.Name.Contains("-auto", StringComparison.Ordinal)));
            }
            catch (FileNotFoundException) { }
        }
        return result.OrderByDescending(x => x.CreatedAt).ToList();
    }

    public string? Resolve(string name)
    {
        if (!BackupName.IsMatch(name)) return null;
        var path = Path.Combine(paths.BackupsDirectory, name);
        return File.Exists(path) ? path : null;
    }

    public void Prune(int keepAutomatic = 7)
    {
        gate.Wait();
        try { PruneLocked(keepAutomatic); }
        finally { gate.Release(); }
    }

    private void PruneLocked(int keepAutomatic)
    {
        foreach (var file in List().Where(x => x.Automatic).Skip(Math.Max(0, keepAutomatic)))
            File.Delete(Path.Combine(paths.BackupsDirectory, file.Name));
    }

    public async Task AutoBackupIfDueAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (Directory.Exists(paths.BackupsDirectory))
                foreach (var stale in Directory.EnumerateFiles(paths.BackupsDirectory, "*.tmp"))
                    if (File.GetLastWriteTimeUtc(stale) < DateTime.UtcNow.AddHours(-1)) TryDelete(stale);
            var latest = List().FirstOrDefault(x => x.Automatic);
            if (latest is not null && DateTime.Now - latest.CreatedAt < TimeSpan.FromHours(24)) return;
            if (!File.Exists(paths.DatabasePath)) return;
            using (var scope = scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                if (!await db.Students.AnyAsync(ct)) return;
            }
            await CreateLockedAsync(true, ct);
            PruneLocked(7);
        }
        finally { gate.Release(); }
    }

    public async Task<RestoreResultDto> RestoreAsync(Stream zip, CancellationToken ct = default, Func<bool>? stillAuthorized = null)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (stillAuthorized?.Invoke() == false) throw new UnauthorizedAccessException("Сессия завершена — войдите снова.");
            return await RestoreLockedAsync(zip, ct);
        }
        finally { gate.Release(); }
    }

    /// Обычные API-запросы регистрируются здесь, чтобы восстановление дождалось их и не потеряло изменения.
    public bool TryEnterRequest()
    {
        Interlocked.Increment(ref inflight);
        if (Volatile.Read(ref maintenance) == 0) return true;
        Interlocked.Decrement(ref inflight);
        return false;
    }

    public void ExitRequest() => Interlocked.Decrement(ref inflight);

    private async Task EnterMaintenanceAsync(CancellationToken ct)
    {
        Volatile.Write(ref maintenance, 1);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (Volatile.Read(ref inflight) > 0)
        {
            if (DateTime.UtcNow > deadline)
            {
                Volatile.Write(ref maintenance, 0);
                throw new InvalidOperationException("Сервер занят другими запросами — повторите восстановление через минуту.");
            }
            await Task.Delay(50, ct);
        }
    }

    private async Task<RestoreResultDto> RestoreLockedAsync(Stream zip, CancellationToken ct)
    {
        Directory.CreateDirectory(paths.BackupsDirectory);
        var work = Path.Combine(paths.BackupsDirectory, $"{Guid.NewGuid():N}.restore.tmp");
        Directory.CreateDirectory(work);
        var upload = Path.Combine(work, "upload.zip");
        var extracted = Path.Combine(work, "bistudent.db");
        var keysDirectory = Path.Combine(work, "keys");
        var replaced = false;
        try
        {
            await using (var output = File.Create(upload))
                await CopyLimitedAsync(zip, output, MaxZipSize, "Файл копии больше 200 МБ.", ct);
            var keys = new List<string>();
            int students, marks;
            try
            {
                await using var archiveStream = File.OpenRead(upload);
                using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
                if (archive.Entries.Count > MaxEntries) throw InvalidBackup();
                var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
                foreach (var entry in archive.Entries)
                {
                    if (entry.FullName is not ("manifest.json" or "bistudent.db") && !KeyName.IsMatch(entry.FullName))
                        throw InvalidBackup();
                    if (!entries.TryAdd(entry.FullName, entry)) throw InvalidBackup();
                }
                if (!entries.TryGetValue("manifest.json", out var manifest) ||
                    !entries.TryGetValue("bistudent.db", out var database)) throw InvalidBackup();
                using (var manifestBytes = new MemoryStream())
                {
                    await using (var input = manifest.Open())
                        await CopyLimitedAsync(input, manifestBytes, MaxManifestSize, null, ct);
                    try
                    {
                        using var json = JsonDocument.Parse(manifestBytes.ToArray());
                        if (json.RootElement.ValueKind != JsonValueKind.Object ||
                            !json.RootElement.TryGetProperty("app", out var app) || app.ValueKind != JsonValueKind.String ||
                            app.GetString() != "BiStudent" ||
                            !json.RootElement.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.Number ||
                            !format.TryGetInt32(out var version) || version != 1) throw InvalidBackup();
                    }
                    catch (JsonException) { throw InvalidBackup(); }
                }
                await using (var output = File.Create(extracted))
                await using (var input = database.Open())
                    await CopyLimitedAsync(input, output, MaxDatabaseSize, null, ct);
                Directory.CreateDirectory(keysDirectory);
                foreach (var entry in entries.Values.Where(x => KeyName.IsMatch(x.FullName)))
                {
                    var staged = Path.Combine(keysDirectory, entry.Name);
                    await using (var output = File.Create(staged))
                    await using (var input = entry.Open())
                        await CopyLimitedAsync(input, output, MaxKeySize, null, ct);
                    keys.Add(staged);
                }
                (students, marks) = await ValidateAsync(extracted, ct);
            }
            catch (InvalidDataException) { throw InvalidBackup(); }

            await EnterMaintenanceAsync(ct);
            var insurance = await CreateLockedAsync(false, ct);
            Directory.CreateDirectory(paths.KeysDirectory);
            var restart = false;
            foreach (var staged in keys)
            {
                var target = Path.Combine(paths.KeysDirectory, Path.GetFileName(staged));
                if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(File.ReadAllBytes(staged))) continue;
                var temporary = target + ".tmp";
                File.Copy(staged, temporary, overwrite: true);
                File.Move(temporary, target, overwrite: true);
                restart = true;
            }
            sessions.Rotate();
            replaced = true;
            using (var source = new SqliteConnection(ReadOnlyConnection(extracted)))
            using (var destination = new SqliteConnection(paths.ConnectionString))
            {
                source.Open();
                destination.Open();
                source.BackupDatabase(destination);
            }
            SqliteConnection.ClearAllPools();
            var message = $"Восстановлено: {students} студентов, {marks} отметок. Текущие данные сохранены в копии {insurance.Name}. Все пользователи должны войти заново." +
                (restart ? " Перезапустите сервер, чтобы образцы лиц из копии расшифровались." : "");
            logger.LogInformation("Восстановлена резервная копия; страховочная копия {Name}.", insurance.Name);
            return new RestoreResultDto(true, message, students, marks, restart);
        }
        finally
        {
            if (replaced) recognition.Invalidate();
            Volatile.Write(ref maintenance, 0);
            try { Directory.Delete(work, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning(ex, "Не удалось удалить временную папку {Path}.", work); }
        }
    }

    private static async Task CopyLimitedAsync(Stream input, Stream output, long limit, string? message, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) != 0)
        {
            total += read;
            if (total > limit) throw message is null ? InvalidBackup() : new InvalidOperationException(message);
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    private static InvalidOperationException InvalidBackup() => new("Это не резервная копия BiStudent.");
    private static string ReadOnlyConnection(string path) => new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();

    private static async Task<(int Students, int Marks)> CountsAsync(string path, CancellationToken ct)
    {
        using var connection = new SqliteConnection(ReadOnlyConnection(path));
        await connection.OpenAsync(ct);
        return (await CountAsync(connection, "Students", ct), await CountAsync(connection, "AttendanceMarks", ct));
    }

    private async Task<(int Students, int Marks)> ValidateAsync(string path, CancellationToken ct)
    {
        using var connection = new SqliteConnection(ReadOnlyConnection(path));
        await connection.OpenAsync(ct);
        using var integrity = connection.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check";
        if ((string?)await integrity.ExecuteScalarAsync(ct) != "ok") throw InvalidBackup();
        var expected = await SchemaAsync(ReferenceConnection(), ct);
        var actual = await SchemaAsync(connection, ct);
        foreach (var (table, definition) in expected)
            if (!actual.TryGetValue(table, out var found) || found != definition)
                throw new InvalidOperationException($"Копия от несовместимой версии BiStudent: отличается таблица {table}.");
        using var foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_key_check";
        await using (var reader = await foreignKeys.ExecuteReaderAsync(ct))
            if (await reader.ReadAsync(ct)) throw new InvalidOperationException("В копии нарушены связи между таблицами.");
        return (await CountAsync(connection, "Students", ct), await CountAsync(connection, "AttendanceMarks", ct));
    }

    private SqliteConnection ReferenceConnection()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        using var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        return connection;
    }

    /// Описание схемы для сравнения: столбцы (имя, тип, NOT NULL, PK) и внешние ключи каждой таблицы.
    private static async Task<Dictionary<string, string>> SchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
        var tables = new List<string>();
        using (var list = connection.CreateCommand())
        {
            list.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            await using var reader = await list.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) tables.Add(reader.GetString(0));
        }
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            var parts = new List<string>();
            using (var columns = connection.CreateCommand())
            {
                columns.CommandText = "SELECT name, upper(type), \"notnull\", pk FROM pragma_table_info($table) ORDER BY name";
                columns.Parameters.AddWithValue("$table", table);
                await using var reader = await columns.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    parts.Add($"c:{reader.GetString(0)}:{reader.GetString(1)}:{reader.GetInt64(2)}:{reader.GetInt64(3)}");
            }
            using (var keys = connection.CreateCommand())
            {
                keys.CommandText = "SELECT \"table\", \"from\", \"to\", on_delete FROM pragma_foreign_key_list($table) ORDER BY \"from\"";
                keys.Parameters.AddWithValue("$table", table);
                await using var reader = await keys.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    parts.Add($"f:{reader.GetString(0)}:{reader.GetString(1)}:{(reader.IsDBNull(2) ? "" : reader.GetString(2))}:{reader.GetString(3)}");
            }
            result[table] = string.Join("|", parts);
        }
        return result;
    }

    private static async Task<int> CountAsync(SqliteConnection connection, string table, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }

    public sealed class AutoBackupWorker(BackupService backups, ILogger<AutoBackupWorker> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await backups.AutoBackupIfDueAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogError(ex, "Не удалось создать автоматическую резервную копию."); }
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
        }
    }
}
