using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BiStudent.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace BiStudent.Tests;

public sealed class EnrollmentApiTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly string temporaryDirectory = Path.Combine(Path.GetTempPath(), $"BiStudent.Tests.{Guid.NewGuid():N}");
    private readonly WebApplicationFactory<Program> factory;
    private readonly HttpClient client;

    public EnrollmentApiTests()
    {
        Directory.CreateDirectory(temporaryDirectory);
        factory = new EnrollmentFactory(temporaryDirectory);
        client = factory.CreateClient();
    }

    [Fact]
    public async Task EnrollmentCodeLifecycleAndMissingSubmissions()
    {
        await AuthenticateAsync();
        var group = await PostAsync<GroupDto>("/api/groups", new CreateGroupRequest("Группа регистрации"));
        var student = await PostAsync<StudentDto>("/api/students", new SaveStudentRequest("Студент регистрации", group.Id));
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync($"/api/groups/{group.Id}/enrollment-code")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/groups/{group.Id}/enrollment-code", new CreateEnrollmentCodeRequest(0), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/groups/{group.Id}/enrollment-code", new CreateEnrollmentCodeRequest(200), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/groups/999999/enrollment-code", new CreateEnrollmentCodeRequest(), JsonOptions)).StatusCode);
        var code = await PostAsync<EnrollmentCodeDto>($"/api/groups/{group.Id}/enrollment-code", new CreateEnrollmentCodeRequest());
        Assert.NotEmpty(code.Links);
        Assert.All(code.Links, link => Assert.EndsWith("/register/", link));
        var check = await (await client.PostAsJsonAsync("/api/public/enrollment/check", new PublicCodeRequest(code.Code), JsonOptions)).Content.ReadFromJsonAsync<PublicGroupDto>(JsonOptions);
        Assert.Contains(check!.Students, x => x.Id == student.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/public/enrollment/submit", new PublicSubmitRequest("INVALID", student.Id, [], true), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/public/enrollment/submit", new PublicSubmitRequest(code.Code, student.Id, [null!, "x", "y"], true), JsonOptions)).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<List<FaceSubmissionDto>>("/api/face-submissions", JsonOptions))!);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/face-submissions/999/approve", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/face-submissions/999/reject", new RejectSubmissionRequest(null), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/face-submissions/999/images/0")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/groups/{group.Id}/enrollment-code")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync($"/api/groups/{group.Id}/enrollment-code")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/public/enrollment/submit", new PublicSubmitRequest(code.Code, student.Id, [], true), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/public/enrollment/check", new PublicCodeRequest("INVALID"), JsonOptions)).StatusCode);
    }

    [Fact]
    public async Task ThirtyFirstPublicCheckIsRateLimited()
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var response = await client.PostAsJsonAsync("/api/public/enrollment/check", new PublicCodeRequest("INVALID"), JsonOptions);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        var limited = await client.PostAsJsonAsync("/api/public/enrollment/check", new PublicCodeRequest("INVALID"), JsonOptions);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        var error = await limited.Content.ReadFromJsonAsync<ErrorDto>(JsonOptions);
        Assert.False(string.IsNullOrWhiteSpace(error!.Message));
    }

    private async Task<T> PostAsync<T>(string path, object value)
    {
        var response = await client.PostAsJsonAsync(path, value, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<T>(JsonOptions);
        return result!;
    }

    private async Task AuthenticateAsync()
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("teacher", "teacher123"), JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(JsonOptions);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public void Dispose()
    {
        client.Dispose();
        factory.Dispose();
        Directory.Delete(temporaryDirectory, recursive: true);
    }

    private sealed class EnrollmentFactory(string directory) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(ServerRoot());
            builder.UseSetting("ConnectionStrings:Default", $"Data Source={Path.Combine(directory, "test.db")};Pooling=False");
            builder.UseSetting("Demo:Enabled", "true");
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
