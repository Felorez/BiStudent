using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BiStudent.Contracts;
using BiStudent.Server.Data;
using BiStudent.Server.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BiStudent.Tests;

public sealed class EnrollmentTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly string temporaryDirectory = Path.Combine(Path.GetTempPath(), $"BiStudent.EnrollmentTests.{Guid.NewGuid():N}");
    private readonly WebApplicationFactory<Program> factory;
    private readonly HttpClient client;

    public EnrollmentTests()
    {
        Directory.CreateDirectory(temporaryDirectory);
        factory = new EnrollmentFactory(temporaryDirectory);
        client = factory.CreateClient();
    }

    [Fact]
    public async Task Code_CanBeCheckedAndReplacementRevokesOldCode()
    {
        await AuthenticateAsync();
        var group = (await client.GetFromJsonAsync<List<GroupDto>>("/api/groups", JsonOptions))!.First(g => g.Students > 0);
        var first = await CreateCodeAsync(group.Id);
        client.DefaultRequestHeaders.Authorization = null;
        var lower = first.Code.ToLowerInvariant().Replace("-", "");
        var check = await client.PostAsJsonAsync("/api/public/enrollment/check", new PublicCodeRequest(lower), JsonOptions);
        Assert.Equal(HttpStatusCode.OK, check.StatusCode);
        var found = await check.Content.ReadFromJsonAsync<PublicGroupDto>(JsonOptions);
        Assert.Equal(group.Name, found!.GroupName);
        Assert.NotEmpty(found.Students);
        await AuthenticateAsync();
        var second = await CreateCodeAsync(group.Id);
        Assert.NotEqual(first.Code, second.Code);
        client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync("/api/public/enrollment/check", new PublicCodeRequest(first.Code), JsonOptions)).StatusCode);
    }

    [Fact]
    public async Task InvalidAndRevokedCodesReturnNotFound()
    {
        var invalid = await client.PostAsJsonAsync("/api/public/enrollment/check", new PublicCodeRequest("BAD-CODE"), JsonOptions);
        Assert.Equal(HttpStatusCode.NotFound, invalid.StatusCode);
        await AuthenticateAsync();
        var group = (await client.GetFromJsonAsync<List<GroupDto>>("/api/groups", JsonOptions))!.First();
        var code = await CreateCodeAsync(group.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/groups/{group.Id}/enrollment-code")).StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync("/api/public/enrollment/check", new PublicCodeRequest(code.Code), JsonOptions)).StatusCode);
    }

    [Fact]
    public async Task SubmitValidatesConsentCountAndBase64()
    {
        await AuthenticateAsync();
        var group = (await client.GetFromJsonAsync<List<GroupDto>>("/api/groups", JsonOptions))!.First();
        var student = (await client.GetFromJsonAsync<List<StudentDto>>($"/api/groups/{group.Id}/students", JsonOptions))!.First();
        var code = await CreateCodeAsync(group.Id);
        client.DefaultRequestHeaders.Authorization = null;
        async Task<HttpStatusCode> Submit(List<string> images, bool consent) =>
            (await client.PostAsJsonAsync("/api/public/enrollment/submit",
                new PublicSubmitRequest(code.Code, student.Id, images, consent), JsonOptions)).StatusCode;
        Assert.Equal(HttpStatusCode.BadRequest, await Submit(["a", "b", "c"], false));
        Assert.Equal(HttpStatusCode.BadRequest, await Submit(["a", "b"], true));
        Assert.Equal(HttpStatusCode.BadRequest, await Submit(["bad!", "bad!", "bad!"], true));
        Assert.Equal(HttpStatusCode.BadRequest, await Submit([null!, "a", "b"], true));
        Assert.Equal(HttpStatusCode.BadRequest, await Submit(null!, true));
    }

    [Fact]
    public async Task ReviewMovesEncryptedCropsToSamplesAndRejectsAnotherSubmission()
    {
        await AuthenticateAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var recognition = scope.ServiceProvider.GetRequiredService<RecognitionService>();
        var service = scope.ServiceProvider.GetRequiredService<EnrollmentService>();
        var teacher = await db.Teachers.FirstAsync();
        var student = await db.Students.FirstAsync();
        var first = new FaceSubmission {
            StudentId = student.Id, Status = FaceSubmissionStatus.Pending,
            Images = Enumerable.Range(0, 3).Select(index => new FaceSubmissionImage {
                ProtectedImage = recognition.ProtectImage([(byte)index])
            }).ToList()
        };
        db.FaceSubmissions.Add(first);
        await db.SaveChangesAsync();
        await service.ApproveAsync(teacher.Id, first.Id);
        db.ChangeTracker.Clear();
        Assert.Equal(3, await db.FaceSamples.CountAsync(x => x.StudentId == student.Id));
        Assert.Equal(0, await db.FaceSubmissionImages.CountAsync(x => x.SubmissionId == first.Id));
        Assert.Equal(FaceSubmissionStatus.Approved, (await db.FaceSubmissions.SingleAsync(x => x.Id == first.Id)).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApproveAsync(teacher.Id, first.Id));
        var second = new FaceSubmission {
            StudentId = student.Id, Status = FaceSubmissionStatus.Pending,
            Images = Enumerable.Range(0, 3).Select(index => new FaceSubmissionImage {
                ProtectedImage = recognition.ProtectImage([(byte)index])
            }).ToList()
        };
        db.FaceSubmissions.Add(second);
        await db.SaveChangesAsync();
        await service.RejectAsync(teacher.Id, second.Id, "  Неверные снимки  ");
        db.ChangeTracker.Clear();
        var rejected = await db.FaceSubmissions.SingleAsync(x => x.Id == second.Id);
        Assert.Equal(FaceSubmissionStatus.Rejected, rejected.Status);
        Assert.Equal("Неверные снимки", rejected.RejectReason);
        Assert.Equal(0, await db.FaceSubmissionImages.CountAsync(x => x.SubmissionId == second.Id));
    }

    [Fact]
    public async Task ParallelApproveAndRejectOnlyOneSucceeds()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var recognition = scope.ServiceProvider.GetRequiredService<RecognitionService>();
        var teacher = await db.Teachers.FirstAsync();
        var student = await db.Students.FirstAsync();
        var submission = new FaceSubmission {
            StudentId = student.Id, Status = FaceSubmissionStatus.Pending,
            Images = Enumerable.Range(0, 3).Select(index => new FaceSubmissionImage {
                ProtectedImage = recognition.ProtectImage([(byte)index])
            }).ToList()
        };
        db.FaceSubmissions.Add(submission);
        await db.SaveChangesAsync();

        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = 0;
        async Task<bool> ReviewAsync(bool approve)
        {
            using var reviewScope = factory.Services.CreateScope();
            var service = reviewScope.ServiceProvider.GetRequiredService<EnrollmentService>();
            if (Interlocked.Increment(ref ready) == 2) start.SetResult(true);
            await start.Task;
            try
            {
                if (approve) await service.ApproveAsync(teacher.Id, submission.Id);
                else await service.RejectAsync(teacher.Id, submission.Id, null);
                return true;
            }
            catch (InvalidOperationException) { return false; }
        }

        var approval = Task.Run(() => ReviewAsync(true));
        var rejection = Task.Run(() => ReviewAsync(false));
        var results = await Task.WhenAll(approval, rejection);
        Assert.Single(results, x => x);
        db.ChangeTracker.Clear();
        var reviewed = await db.FaceSubmissions.SingleAsync(x => x.Id == submission.Id);
        Assert.Contains(reviewed.Status, new[] { FaceSubmissionStatus.Approved, FaceSubmissionStatus.Rejected });
        Assert.Equal(0, await db.FaceSubmissionImages.CountAsync(x => x.SubmissionId == submission.Id));
        Assert.Equal(reviewed.Status == FaceSubmissionStatus.Approved ? 3 : 0,
            await db.FaceSamples.CountAsync(x => x.StudentId == student.Id));
    }

    [Fact]
    public async Task RegisterPageReturnsHtml()
    {
        var response = await client.GetAsync("/register/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Регистрация лица", await response.Content.ReadAsStringAsync());
    }

    private async Task<EnrollmentCodeDto> CreateCodeAsync(int groupId)
    {
        var response = await client.PostAsJsonAsync($"/api/groups/{groupId}/enrollment-code", new CreateEnrollmentCodeRequest(), JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EnrollmentCodeDto>(JsonOptions))!;
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
