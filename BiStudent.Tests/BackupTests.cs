using System.IO.Compression;
using BiStudent.Contracts;
using BiStudent.Server.Data;
using BiStudent.Server.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BiStudent.Tests;

public sealed class BackupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider services;
    private readonly RecognitionService recognition;
    private readonly BackupService backups;
    private readonly StoragePaths paths;
    private readonly SessionGeneration sessions;

    public BackupTests()
    {
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "bistudent.db");
        paths = new StoragePaths(database, new SqliteConnectionStringBuilder { DataSource = database }.ToString());
        Directory.CreateDirectory(paths.KeysDirectory);
        File.WriteAllText(Path.Combine(paths.KeysDirectory, "key-11111111-2222-3333-4444-555555555555.xml"), "<key />");
        services = new ServiceCollection().AddDbContext<AppDbContext>(options => options.UseSqlite(paths.ConnectionString)).BuildServiceProvider();
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.EnsureCreated();
            var teacher = new Teacher { Login = "teacher", FullName = "Ирина Иванова" };
            var group = new Group { Name = "10-А" };
            var student = new Student { FullName = "Анна", Group = group };
            var slot = new ScheduleSlot { Teacher = teacher, Group = group, Subject = "Алгебра",
                DayOfWeek = DayOfWeek.Tuesday, Start = new TimeOnly(9, 0), End = new TimeOnly(10, 0),
                ValidFrom = new DateOnly(2026, 9, 1) };
            db.AttendanceMarks.Add(new AttendanceMark { Slot = slot, Student = student,
                Date = new DateOnly(2026, 9, 1), Status = AttendanceStatus.Present, Method = MarkMethod.Manual });
            db.SaveChanges();
        }
        var config = new ConfigurationBuilder().Build();
        recognition = new RecognitionService(services.GetRequiredService<IServiceScopeFactory>(),
            DataProtectionProvider.Create("tests"), config);
        sessions = new SessionGeneration(paths);
        backups = new BackupService(paths, services.GetRequiredService<IServiceScopeFactory>(), recognition, sessions,
            NullLogger<BackupService>.Instance);
    }

    [Fact]
    public async Task CreateIncludesDatabaseManifestAndKeys()
    {
        var backup = await backups.CreateAsync(false);
        Assert.Equal(backup.Name, Assert.Single(backups.List()).Name);
        Assert.Equal(Path.Combine(paths.BackupsDirectory, backup.Name), backups.Resolve(backup.Name));
        Assert.Null(backups.Resolve("../x.zip"));
        Assert.Null(backups.Resolve("other.zip"));
        using var archive = ZipFile.OpenRead(backups.Resolve(backup.Name)!);
        Assert.Contains(archive.Entries, x => x.FullName == "bistudent.db");
        Assert.Contains(archive.Entries, x => x.FullName == "manifest.json");
        Assert.Contains(archive.Entries, x => x.FullName == "keys/key-11111111-2222-3333-4444-555555555555.xml");
    }

    [Fact]
    public async Task RestoreReturnsStudentAndKeepsSafetyCopy()
    {
        var generation = sessions.Current;
        var backup = await backups.CreateAsync(false);
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Students.Remove(await db.Students.SingleAsync());
            await db.SaveChangesAsync();
        }
        await using var zip = File.OpenRead(backups.Resolve(backup.Name)!);
        var result = await backups.RestoreAsync(zip);
        Assert.True(result.Ok);
        Assert.Equal(1, result.Students);
        Assert.Equal(1, result.Marks);
        Assert.False(result.RestartRecommended);
        Assert.Equal(2, backups.List().Count);
        Assert.NotEqual(generation, sessions.Current);
        using var restoredScope = services.CreateScope();
        var restored = restoredScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal("Анна", (await restored.Students.SingleAsync()).FullName);
    }

    [Fact]
    public async Task RestoreRejectsInvalidEntries()
    {
        foreach (var names in new[] { new[] { "manifest.json" }, new[] { "manifest.json", "bistudent.db", "../evil.txt" },
                     new[] { "manifest.json", "bistudent.db", "keys/C:injected.xml" } })
        {
            using var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var name in names)
                {
                    using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                    writer.Write(name == "manifest.json" ? "{\"app\":\"BiStudent\",\"format\":1}" : "x");
                }
            stream.Position = 0;
            await Assert.ThrowsAsync<InvalidOperationException>(() => backups.RestoreAsync(stream));
        }
    }

    [Fact]
    public async Task PruneKeepsNewestAutomaticAndManual()
    {
        var automatic = new List<BackupFileDto>();
        for (var i = 0; i < 4; i++)
        {
            var backup = await backups.CreateAsync(true);
            File.SetLastWriteTimeUtc(backups.Resolve(backup.Name)!, DateTime.UtcNow.AddHours(i - 4));
            automatic.Add(backup);
        }
        var manual = await backups.CreateAsync(false);
        backups.Prune(2);
        var remaining = backups.List();
        Assert.Equal(3, remaining.Count);
        Assert.Contains(remaining, x => x.Name == manual.Name);
        Assert.Contains(remaining, x => x.Name == automatic[2].Name);
        Assert.Contains(remaining, x => x.Name == automatic[3].Name);
    }

    [Fact]
    public async Task RestoreRejectsDatabaseWithIncompatibleSchema()
    {
        var generation = sessions.Current;
        var foreign = Path.Combine(root, "foreign.db");
        using (var connection = new SqliteConnection($"Data Source={foreign};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Teachers(x); CREATE TABLE Students(x); CREATE TABLE AttendanceMarks(x); CREATE TABLE FaceSamples(x); CREATE TABLE ScheduleSlots(x);";
            command.ExecuteNonQuery();
        }
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntryFromFile(foreign, "bistudent.db");
            using var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open());
            writer.Write("{\"app\":\"BiStudent\",\"format\":1}");
        }
        stream.Position = 0;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => backups.RestoreAsync(stream));
        Assert.Contains("несовместимой", error.Message);
        Assert.Equal(generation, sessions.Current);
    }

    public void Dispose()
    {
        recognition.Dispose();
        services.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(root, recursive: true);
    }
}
