using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BiStudent.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace BiStudent.Tests;

public sealed class RecognitionSettingsTests : IDisposable
{
    private readonly string temporaryDirectory = Path.Combine(Path.GetTempPath(), $"BiStudent.Tests.{Guid.NewGuid():N}");
    private readonly WebApplicationFactory<Program> factory;
    private readonly HttpClient client;

    public RecognitionSettingsTests()
    {
        Directory.CreateDirectory(temporaryDirectory);
        factory = new ApiFactory(temporaryDirectory);
        client = factory.CreateClient();
    }

    [Fact]
    public async Task GetSettings_ReturnsDefaults()
    {
        await AuthenticateAsync();
        var settings = await client.GetFromJsonAsync<RecognitionSettingsDto>("/api/settings/recognition");
        Assert.NotNull(settings);
        Assert.Equal(0.43, settings.MatchThreshold);
        Assert.Equal(0.06, settings.MinimumMargin);
        Assert.Equal(3, settings.FramesInRow);
    }

    [Fact]
    public async Task SaveSettings_PersistsValues()
    {
        await AuthenticateAsync();
        var expected = new RecognitionSettingsDto(0.55, 0.08, 5);
        var response = await client.PutAsJsonAsync("/api/settings/recognition", expected);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await response.Content.ReadFromJsonAsync<RecognitionSettingsDto>();
        Assert.Equal(expected, saved);
        var loaded = await client.GetFromJsonAsync<RecognitionSettingsDto>("/api/settings/recognition");
        Assert.Equal(expected, loaded);
    }

    [Fact]
    public async Task SaveSettings_WithInvalidThresholdReturnsBadRequest()
    {
        await AuthenticateAsync();
        var response = await client.PutAsJsonAsync("/api/settings/recognition", new RecognitionSettingsDto(0.9, 0.06, 3));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>();
        Assert.NotNull(error);
        Assert.Contains("0.30–0.70", error.Message);
    }

    [Fact]
    public async Task Automation_UsesSavedFramesInRow()
    {
        await AuthenticateAsync();
        var response = await client.PutAsJsonAsync("/api/settings/recognition", new RecognitionSettingsDto(0.43, 0.06, 5));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var automation = await client.GetFromJsonAsync<AutomationSettingsDto>("/api/settings/automation");
        Assert.NotNull(automation);
        Assert.Equal(5, automation.FramesInRow);
    }

    private async Task AuthenticateAsync()
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("teacher", "teacher123"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
    }

    public void Dispose()
    {
        client.Dispose();
        factory.Dispose();
        Directory.Delete(temporaryDirectory, recursive: true);
    }

    private sealed class ApiFactory(string directory) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(ServerRoot());
            builder.UseSetting("ConnectionStrings:Default", $"Data Source={Path.Combine(directory, "test.db")};Pooling=False");
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
