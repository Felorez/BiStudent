using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BiStudent.Contracts;
using BiStudent.Server.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace BiStudent.Tests;

public sealed class AuthTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"BiStudent.AuthTests.{Guid.NewGuid():N}");

    public AuthTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task EmptyDatabase_CreatesAdminWhoMustChangePassword()
    {
        using var factory = new AuthFactory(directory);
        using var client = factory.CreateClient();
        var token = await LoginAsync(client, "admin", "admin");
        Authorize(client, token);
        var me = await client.GetFromJsonAsync<TeacherDto>("/api/me");
        Assert.NotNull(me);
        Assert.True(me.IsAdmin);
        Assert.True(me.MustChangePassword);
    }

    [Fact]
    public async Task ChangedPassword_SurvivesRestart()
    {
        using (var factory = new AuthFactory(directory))
        using (var client = factory.CreateClient())
        {
            Authorize(client, await LoginAsync(client, "admin", "admin"));
            var response = await client.PostAsJsonAsync("/api/auth/change-password",
                new ChangePasswordRequest("admin", "new-secret-123"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            client.DefaultRequestHeaders.Authorization = null;
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "admin"))).StatusCode);
            await LoginAsync(client, "admin", "new-secret-123");
        }
        using var restarted = new AuthFactory(directory);
        using var afterRestart = restarted.CreateClient();
        await LoginAsync(afterRestart, "admin", "new-secret-123");
    }

    [Fact]
    public async Task Refresh_StopsWorkingAfterPasswordChange()
    {
        using var factory = new AuthFactory(directory);
        using var client = factory.CreateClient();
        var token = await LoginAsync(client, "admin", "admin");
        Assert.NotNull(token.RefreshToken);
        var refreshed = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(token.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace((await refreshed.Content.ReadFromJsonAsync<TokenResponse>())?.AccessToken));
        Authorize(client, token);
        var changed = await client.PostAsJsonAsync("/api/auth/change-password",
            new ChangePasswordRequest("admin", "new-secret-123"));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(token.RefreshToken))).StatusCode);
    }

    [Fact]
    public async Task Admin_ManagesTeacherAccounts()
    {
        using var factory = new AuthFactory(directory);
        using var client = factory.CreateClient();
        Authorize(client, await LoginAsync(client, "admin", "admin"));
        var created = await client.PostAsJsonAsync("/api/admin/teachers",
            new SaveTeacherRequest("anna", "Анна Иванова", false, true));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var temporary = await created.Content.ReadFromJsonAsync<TemporaryPasswordDto>();
        Assert.NotNull(temporary);
        var accounts = await client.GetFromJsonAsync<List<TeacherAccountDto>>("/api/admin/teachers");
        var account = Assert.Single(accounts!, x => x.Login == "anna");
        using var teacherClient = factory.CreateClient();
        Authorize(teacherClient, await LoginAsync(teacherClient, "anna", temporary.Password));
        var me = await teacherClient.GetFromJsonAsync<TeacherDto>("/api/me");
        Assert.True(me!.MustChangePassword);
        Assert.Equal(HttpStatusCode.Forbidden, (await teacherClient.GetAsync("/api/admin/teachers")).StatusCode);
        var disabled = await client.PutAsJsonAsync($"/api/admin/teachers/{account.Id}",
            new SaveTeacherRequest("anna", "Анна Иванова", false, false));
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        using var loginClient = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Forbidden,
            (await loginClient.PostAsJsonAsync("/api/auth/login", new LoginRequest("anna", temporary.Password))).StatusCode);
    }

    [Fact]
    public void LegacyDatabase_ReceivesBaselineAndAllMigrations()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        using var db = new AppDbContext(options);
        db.GetService<IMigrator>().Migrate(DatabaseInitializer.BaselineMigration);
        db.Database.ExecuteSqlRaw("DROP TABLE \"__EFMigrationsHistory\"");
        DatabaseInitializer.Initialize(db);
        Assert.Equal(db.Database.GetMigrations(), db.Database.GetAppliedMigrations());
    }

    private static async Task<TokenResponse> LoginAsync(HttpClient client, string login, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(login, password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token;
    }

    private static void Authorize(HttpClient client, TokenResponse token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private sealed class AuthFactory(string directory) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(ServerRoot());
            builder.UseSetting("ConnectionStrings:Default", $"Data Source={Path.Combine(directory, "test.db")};Pooling=False");
            builder.UseSetting("Demo:Enabled", "false");
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
    }
}
