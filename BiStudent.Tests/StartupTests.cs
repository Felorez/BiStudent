using System.Net;
using System.Net.Http.Json;
using BiStudent.Contracts;
using BiStudent.Server.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BiStudent.Tests;

public sealed class StartupTests
{
    [Fact]
    public async Task ConfiguredDataDirectoryContainsCreatedDatabase()
    {
        var root = NewRoot();
        try
        {
            var data = Path.Combine(root, "data");
            using (var factory = new StartupFactory(ServerRoot(), data))
            using (var client = factory.CreateClient())
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);

            Assert.True(File.Exists(Path.Combine(data, "bistudent.db")));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task LegacyDatabaseAndKeysAreCopiedBeforeLogin()
    {
        var root = NewRoot();
        try
        {
            var legacy = Path.Combine(root, "App_Data");
            var keys = Path.Combine(legacy, "keys");
            var data = Path.Combine(root, "new-data");
            Directory.CreateDirectory(keys);
            var oldDatabase = Path.Combine(legacy, "bistudent.db");
            var oldKey = Path.Combine(keys, "marker.bin");
            File.WriteAllText(oldKey, "legacy key");
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(new SqliteConnectionStringBuilder { DataSource = oldDatabase, Pooling = false }.ToString())
                .Options;
            using (var db = new AppDbContext(options))
            {
                DatabaseInitializer.Initialize(db);
                var teacher = new Teacher {
                    Login = "legacy", FullName = "Старый преподаватель", IsAdmin = true
                };
                teacher.PasswordHash = new PasswordHasher<Teacher>().HashPassword(teacher, "legacy123");
                db.Teachers.Add(teacher);
                db.SaveChanges();
            }

            using (var factory = new StartupFactory(root, data))
            using (var client = factory.CreateClient())
            {
                var response = await client.PostAsJsonAsync("/api/auth/login",
                    new LoginRequest("legacy", "legacy123"));
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.NotNull(await response.Content.ReadFromJsonAsync<TokenResponse>());
            }

            Assert.True(File.Exists(oldDatabase));
            Assert.True(File.Exists(Path.Combine(data, "bistudent.db")));
            Assert.Equal(File.ReadAllText(oldKey), File.ReadAllText(Path.Combine(data, "keys", "marker.bin")));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void InvalidLegacyDatabaseStopsStartupWithoutCreatingReplacement()
    {
        var root = NewRoot();
        try
        {
            var legacy = Path.Combine(root, "App_Data");
            var data = Path.Combine(root, "new-data");
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "bistudent.db"), "not a SQLite database");

            using var factory = new StartupFactory(root, data);
            Assert.ThrowsAny<Exception>(() => factory.CreateClient());
            Assert.False(File.Exists(Path.Combine(data, "bistudent.db")));
        }
        finally { DeleteRoot(root); }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"BiStudent.Startup.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(root, recursive: true);
    }

    private static string ServerRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "BiStudent.Server");
            if (File.Exists(Path.Combine(candidate, "seed", "students.demo.csv"))) return candidate;
        }
        throw new DirectoryNotFoundException("BiStudent.Server/seed/students.demo.csv");
    }

    private sealed class StartupFactory(string contentRoot, string dataDirectory) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(contentRoot);
            builder.UseSetting("Storage:DataDirectory", dataDirectory);
            builder.UseSetting("Demo:Enabled", "false");
        }
    }
}
