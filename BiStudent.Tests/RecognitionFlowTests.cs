using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BiStudent.Contracts;
using BiStudent.Server.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BiStudent.Tests;

public sealed class RecognitionFlowTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly string temporaryDirectory = Path.Combine(Path.GetTempPath(), $"BiStudent.RecognitionFlowTests.{Guid.NewGuid():N}");
    private readonly WebApplicationFactory<Program> factory;
    private readonly HttpClient client;

    public RecognitionFlowTests()
    {
        Directory.CreateDirectory(temporaryDirectory);
        factory = new RecognitionFactory(temporaryDirectory);
        client = factory.CreateClient();
    }

    [Fact]
    public async Task Enrollment_AcceptsPortraitsAndFlagsSimilarStudent()
    {
        await AuthenticateAsync();
        var group = await CreateGroupAsync();
        var first = await CreateStudentAsync(group.Id, "Первый");
        var second = await CreateStudentAsync(group.Id, "Второй");
        var images = FaceFixtures.EnrollmentSet("armstrong");
        var enrolled = await EnrollAsync(first.Id, images);
        Assert.True(enrolled.Ok, enrolled.Message);
        Assert.True((await StudentsAsync(group.Id)).Single(x => x.Id == first.Id).HasFace);
        var similar = await EnrollAsync(second.Id, images);
        Assert.False(similar.Ok);
        Assert.Equal(first.FullName, similar.SimilarTo);
        var allowed = await EnrollAsync(second.Id, images, true);
        Assert.True(allowed.Ok, allowed.Message);
    }

    [Fact]
    public async Task Enrollment_RejectsMissingRepeatedFarAndCorruptFrames()
    {
        await AuthenticateAsync();
        var group = await CreateGroupAsync();
        var student = await CreateStudentAsync(group.Id, "Студент");
        var images = FaceFixtures.EnrollmentSet("armstrong");
        Assert.False((await EnrollAsync(student.Id, images.Take(2).ToList())).Ok);
        var repeated = await EnrollAsync(student.Id, [images[0], images[0], images[0]]);
        Assert.False(repeated.Ok);
        Assert.Contains(repeated.Problems!, problem => problem.Contains("почти одинаковые"));
        using var portrait = FaceFixtures.Load("armstrong");
        using var far = FaceFixtures.Far(portrait);
        var farImages = Enumerable.Repeat(FaceFixtures.Jpeg(far), 3).ToList();
        var distant = await EnrollAsync(student.Id, farImages);
        Assert.False(distant.Ok);
        Assert.Contains(distant.Problems!, problem => problem.Contains("далеко"));
        var corrupt = await EnrollAsync(student.Id, [[1, 2, 3], [1, 2, 3], [1, 2, 3]]);
        Assert.False(corrupt.Ok);
        Assert.NotEmpty(corrupt.Problems!);
    }

    [Fact]
    public async Task Recognition_MarksOnlyAfterRequiredFrames()
    {
        await AuthenticateAsync();
        var group = await CreateGroupAsync();
        var student = await CreateStudentAsync(group.Id, "Армстронг");
        var slot = await CreateSlotAsync(group.Id);
        Assert.True((await EnrollAsync(student.Id, FaceFixtures.EnrollmentSet("armstrong"))).Ok);
        var settings = await client.GetFromJsonAsync<AutomationSettingsDto>("/api/settings/automation", JsonOptions);
        Assert.NotNull(settings);
        using var portrait = FaceFixtures.Load("armstrong");
        using var bright = FaceFixtures.Brighter(portrait);
        var jpeg = FaceFixtures.Jpeg(bright);
        var date = slot.OneOffDate!.Value;
        for (var frame = 1; frame < settings.FramesInRow; frame++)
        {
            var result = await RecognizeAsync(slot.Id, date, jpeg);
            Assert.False(Assert.Single(result.Faces).Marked);
            Assert.Null((await AttendanceAsync(slot.Id, date)).Single(row => row.StudentId == student.Id).Status);
        }
        var final = await RecognizeAsync(slot.Id, date, jpeg);
        var face = Assert.Single(final.Faces);
        Assert.Equal(student.Id, face.StudentId);
        Assert.True(face.Marked || face.AlreadyMarked);
        var row = (await AttendanceAsync(slot.Id, date)).Single(item => item.StudentId == student.Id);
        Assert.Equal(AttendanceStatus.Present, row.Status);
        Assert.Equal(MarkMethod.Camera, row.Method);
        Assert.True(Assert.Single((await RecognizeAsync(slot.Id, date, jpeg)).Faces).AlreadyMarked);
    }

    [Fact]
    public async Task Recognition_DoesNotOverrideManualAbsence()
    {
        await AuthenticateAsync();
        var group = await CreateGroupAsync();
        var student = await CreateStudentAsync(group.Id, "Армстронг");
        var slot = await CreateSlotAsync(group.Id);
        Assert.True((await EnrollAsync(student.Id, FaceFixtures.EnrollmentSet("armstrong"))).Ok);
        var date = slot.OneOffDate!.Value;
        var response = await client.PutAsJsonAsync($"/api/lessons/{slot.Id}/{date:yyyy-MM-dd}/attendance",
            new MarkRequest(student.Id, AttendanceStatus.Absent), JsonOptions);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var settings = await client.GetFromJsonAsync<AutomationSettingsDto>("/api/settings/automation", JsonOptions);
        using var portrait = FaceFixtures.Load("armstrong");
        using var bright = FaceFixtures.Brighter(portrait);
        var jpeg = FaceFixtures.Jpeg(bright);
        for (var i = 0; i < settings!.FramesInRow; i++)
            await RecognizeAsync(slot.Id, date, jpeg);
        var row = (await AttendanceAsync(slot.Id, date)).Single(item => item.StudentId == student.Id);
        Assert.Equal(AttendanceStatus.Absent, row.Status);
        Assert.Equal(MarkMethod.Manual, row.Method);
    }

    [Fact]
    public async Task Recognition_LeavesUnknownAndOtherGroupUnmarked()
    {
        await AuthenticateAsync();
        var group = await CreateGroupAsync();
        var own = await CreateStudentAsync(group.Id, "Свой студент");
        var slot = await CreateSlotAsync(group.Id);
        var otherGroup = await CreateGroupAsync();
        var visitor = await CreateStudentAsync(otherGroup.Id, "Студент другой группы");
        Assert.True((await EnrollAsync(visitor.Id, FaceFixtures.EnrollmentSet("aldrin"))).Ok);
        var date = slot.OneOffDate!.Value;
        using var ride = FaceFixtures.Load("ride");
        var unknown = Assert.Single((await RecognizeAsync(slot.Id, date, FaceFixtures.Jpeg(ride))).Faces);
        Assert.Null(unknown.StudentId);
        using var aldrin = FaceFixtures.Load("aldrin");
        var outside = Assert.Single((await RecognizeAsync(slot.Id, date, FaceFixtures.Jpeg(aldrin))).Faces);
        Assert.Equal(visitor.Id, outside.StudentId);
        Assert.True(outside.OtherGroup);
        Assert.False(outside.Marked);
        Assert.Null((await AttendanceAsync(slot.Id, date)).Single(row => row.StudentId == own.Id).Status);
    }

    [Fact]
    public async Task RecognitionStatus_IsAvailable()
    {
        await AuthenticateAsync();
        var status = await client.GetFromJsonAsync<EnrollResultDto>("/api/recognition/status", JsonOptions);
        Assert.True(status!.Ok, status.Message);
    }

    [Fact]
    public async Task PublicSubmission_CanBeReviewedAndRecognized()
    {
        await AuthenticateAsync();
        var group = await CreateGroupAsync();
        var student = await CreateStudentAsync(group.Id, "Райд");
        var slot = await CreateSlotAsync(group.Id);
        var code = await CreateCodeAsync(group.Id);
        var images = PublicImages("ride");
        var submitted = await SubmitAsync(code.Code, student.Id, images);
        Assert.True(submitted.Ok, submitted.Message);
        var submissions = await client.GetFromJsonAsync<List<FaceSubmissionDto>>("/api/face-submissions", JsonOptions);
        var submission = Assert.Single(submissions!, item => item.StudentId == student.Id && item.Status == FaceSubmissionStatus.Pending);
        Assert.Equal(3, submission.ImageCount);
        for (var index = 0; index < 3; index++)
        {
            var response = await client.GetAsync($"/api/face-submissions/{submission.Id}/images/{index}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
            Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync());
        }
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/face-submissions/{submission.Id}/images/3")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsync($"/api/face-submissions/{submission.Id}/approve", null)).StatusCode);
        Assert.True((await StudentsAsync(group.Id)).Single(item => item.Id == student.Id).HasFace);
        var settings = await client.GetFromJsonAsync<AutomationSettingsDto>("/api/settings/automation", JsonOptions);
        using var ride = FaceFixtures.Load("ride");
        using var bright = FaceFixtures.Brighter(ride);
        var jpeg = FaceFixtures.Jpeg(bright);
        var date = slot.OneOffDate!.Value;
        RecognitionResultDto? result = null;
        for (var frame = 0; frame < settings!.FramesInRow; frame++)
            result = await RecognizeAsync(slot.Id, date, jpeg);
        Assert.Equal(student.Id, Assert.Single(result!.Faces).StudentId);
        var row = (await AttendanceAsync(slot.Id, date)).Single(item => item.StudentId == student.Id);
        Assert.Equal(AttendanceStatus.Present, row.Status);
        Assert.Equal(MarkMethod.Camera, row.Method);
    }

    [Fact]
    public async Task PublicSubmission_ResubmissionSupersedesPending()
    {
        await AuthenticateAsync();
        var group = await CreateGroupAsync();
        var student = await CreateStudentAsync(group.Id, "Райд");
        var code = await CreateCodeAsync(group.Id);
        var images = PublicImages("ride");
        Assert.True((await SubmitAsync(code.Code, student.Id, images)).Ok);
        Assert.True((await SubmitAsync(code.Code, student.Id, images)).Ok);
        var submissions = await client.GetFromJsonAsync<List<FaceSubmissionDto>>("/api/face-submissions", JsonOptions);
        Assert.Single(submissions!, item => item.StudentId == student.Id && item.Status == FaceSubmissionStatus.Pending);
    }

    [Fact]
    public async Task UnreadableSample_IsReportedWithoutFace()
    {
        await AuthenticateAsync();
        var group = await CreateGroupAsync();
        var student = await CreateStudentAsync(group.Id, "Студент");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.FaceSamples.Add(new FaceSample { StudentId = student.Id, ProtectedImage = [1, 2, 3] });
        await db.SaveChangesAsync();
        var listed = (await StudentsAsync(group.Id)).Single(item => item.Id == student.Id);
        Assert.True(listed.FaceUnreadable);
        Assert.False(listed.HasFace);
        var status = await client.GetFromJsonAsync<EnrollResultDto>("/api/recognition/status", JsonOptions);
        Assert.True(status!.Ok);
        Assert.Contains("не расшифровываются", status.Message);
    }

    private async Task AuthenticateAsync()
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("teacher", "teacher123"), JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(JsonOptions);
        Assert.NotNull(token);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
    }

    private async Task<GroupDto> CreateGroupAsync()
    {
        var response = await client.PostAsJsonAsync("/api/groups",
            new CreateGroupRequest($"Тестовая группа {Guid.NewGuid():N}"), JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<GroupDto>(JsonOptions))!;
    }

    private async Task<StudentDto> CreateStudentAsync(int groupId, string name)
    {
        var response = await client.PostAsJsonAsync("/api/students", new SaveStudentRequest(name, groupId), JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<StudentDto>(JsonOptions))!;
    }

    private async Task<SlotDto> CreateSlotAsync(int groupId)
    {
        var now = DateTime.Now;
        var date = DateOnly.FromDateTime(now);
        var minutes = now.Hour * 60 + now.Minute;
        var start = Math.Max(0, minutes - 10);
        var end = Math.Min(23 * 60 + 59, minutes + 60);
        var request = new SaveSlotRequest(groupId, null, "Распознавание", null, now.DayOfWeek,
            new TimeOnly(start / 60, start % 60), new TimeOnly(end / 60, end % 60), WeekParity.Every, date);
        var lessons = await client.GetFromJsonAsync<List<LessonDto>>(
            $"/api/schedule/day?date={date:yyyy-MM-dd}", JsonOptions);
        foreach (var lesson in lessons!.Where(lesson => lesson.Slot.Start < request.End && lesson.Slot.End > request.Start))
            Assert.Equal(HttpStatusCode.NoContent,
                (await client.DeleteAsync($"/api/schedule/slots/{lesson.Slot.Id}")).StatusCode);
        var response = await client.PostAsJsonAsync("/api/schedule/slots", request, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SlotDto>(JsonOptions))!;
    }

    private async Task<EnrollResultDto> EnrollAsync(int studentId, List<byte[]> images, bool allowSimilar = false)
    {
        var response = await client.PostAsJsonAsync($"/api/students/{studentId}/enroll",
            new EnrollRequest(images, allowSimilar), JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EnrollResultDto>(JsonOptions))!;
    }

    private async Task<List<StudentDto>> StudentsAsync(int groupId) =>
        (await client.GetFromJsonAsync<List<StudentDto>>($"/api/groups/{groupId}/students", JsonOptions))!;

    private async Task<RecognitionResultDto> RecognizeAsync(int slotId, DateOnly date, byte[] image)
    {
        var response = await client.PostAsJsonAsync("/api/recognize", new RecognizeRequest(slotId, date, image), JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<RecognitionResultDto>(JsonOptions);
        Assert.NotNull(result);
        Assert.Null(result.Error);
        return result;
    }

    private async Task<List<AttendanceRowDto>> AttendanceAsync(int slotId, DateOnly date) =>
        (await client.GetFromJsonAsync<List<AttendanceRowDto>>(
            $"/api/lessons/{slotId}/{date:yyyy-MM-dd}/attendance", JsonOptions))!;

    private async Task<EnrollmentCodeDto> CreateCodeAsync(int groupId)
    {
        var response = await client.PostAsJsonAsync($"/api/groups/{groupId}/enrollment-code",
            new CreateEnrollmentCodeRequest(), JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EnrollmentCodeDto>(JsonOptions))!;
    }

    private async Task<PublicSubmitResultDto> SubmitAsync(string code, int studentId, List<string> images)
    {
        var response = await client.PostAsJsonAsync("/api/public/enrollment/submit",
            new PublicSubmitRequest(code, studentId, images, true), JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PublicSubmitResultDto>(JsonOptions))!;
    }

    private static List<string> PublicImages(string name) =>
        FaceFixtures.EnrollmentSet(name).Select(Convert.ToBase64String).ToList();

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

    private sealed class RecognitionFactory(string directory) : WebApplicationFactory<Program>
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
