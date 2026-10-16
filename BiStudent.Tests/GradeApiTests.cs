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

public sealed class GradeApiTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly string temporaryDirectory = Path.Combine(Path.GetTempPath(), $"BiStudent.Tests.{Guid.NewGuid():N}");
    private readonly WebApplicationFactory<Program> factory;
    private readonly HttpClient client;

    public GradeApiTests()
    {
        Directory.CreateDirectory(temporaryDirectory);
        factory = new GradeFactory(temporaryDirectory);
        client = factory.CreateClient();
    }

    [Fact]
    public async Task LessonGradeAcceptsScoresAndClearButRejectsZero()
    {
        await AuthenticateAsync();
        var (group, student, slot, date) = await LessonAsync();
        var path = $"/api/lessons/{slot.Id}/{date:yyyy-MM-dd}/grade";
        var subjects = await client.GetFromJsonAsync<List<string>>($"/api/journal/{group.Id}/subjects", JsonOptions);
        Assert.Contains("Алгебра", subjects!);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(path, new GradeRequest(student.Id, 100), JsonOptions)).StatusCode);
        var attendance = await client.GetFromJsonAsync<List<AttendanceRowDto>>($"/api/lessons/{slot.Id}/{date:yyyy-MM-dd}/attendance", JsonOptions);
        Assert.Equal(100, attendance!.Single(x => x.StudentId == student.Id).Score);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(path, new GradeRequest(student.Id, 1), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(path, new GradeRequest(student.Id, null), JsonOptions)).StatusCode);
        Assert.Null((await client.GetFromJsonAsync<List<AttendanceRowDto>>($"/api/lessons/{slot.Id}/{date:yyyy-MM-dd}/attendance", JsonOptions))!.Single(x => x.StudentId == student.Id).Score);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(path, new GradeRequest(student.Id, 0), JsonOptions)).StatusCode);
    }

    [Fact]
    public async Task AssessmentsAndFinalGradesAppearInJournal()
    {
        await AuthenticateAsync();
        var (group, first, slot, date) = await LessonAsync();
        var second = await PostAsync<StudentDto>("/api/students", new SaveStudentRequest("Второй", group.Id));
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/lessons/{slot.Id}/{date:yyyy-MM-dd}/grade", new GradeRequest(first.Id, 80), JsonOptions)).StatusCode);
        var request = new SaveAssessmentRequest(group.Id, "Алгебра", "Контрольная", AssessmentKind.ControlWork, date);
        var assessment = await PostAsync<AssessmentDto>("/api/assessments", request);
        var listed = await client.GetFromJsonAsync<List<AssessmentDto>>($"/api/assessments?groupId={group.Id}&subject=Алгебра", JsonOptions);
        Assert.Contains(listed!, x => x.Id == assessment.Id);
        var renamed = request with { Title = "Итоговая контрольная" };
        var changed = await client.PutAsJsonAsync($"/api/assessments/{assessment.Id}", renamed, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(renamed.Title, (await changed.Content.ReadFromJsonAsync<AssessmentDto>(JsonOptions))!.Title);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/assessments/{assessment.Id}/grades", new GradeRequest(first.Id, 100), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/assessments/{assessment.Id}/grades", new GradeRequest(second.Id, 70), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/assessments/{assessment.Id}/grades", new GradeRequest(first.Id, 0), JsonOptions)).StatusCode);
        var filled = await (await client.PostAsJsonAsync($"/api/journal/{group.Id}/final/fill", new FillFinalRequest("Алгебра"), JsonOptions)).Content.ReadFromJsonAsync<CountResult>(JsonOptions);
        Assert.Equal(2, filled!.Count);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/journal/{group.Id}/final", new FinalGradeRequest("Алгебра", first.Id, 91), JsonOptions)).StatusCode);
        var journal = await client.GetFromJsonAsync<JournalDto>($"/api/journal/{group.Id}?from={date:yyyy-MM-dd}&to={date:yyyy-MM-dd}&subject=Алгебра", JsonOptions);
        var column = journal!.Columns.FindIndex(x => x.Kind == JournalColumnKind.Assessment && x.Id == assessment.Id);
        Assert.True(column >= 0);
        var firstRow = journal.Students.Single(x => x.StudentId == first.Id);
        var secondRow = journal.Students.Single(x => x.StudentId == second.Id);
        Assert.Equal(100, firstRow.Cells[column].Score);
        Assert.Equal(91, firstRow.Final);
        Assert.Equal(70, secondRow.Final);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/assessments/{assessment.Id}/grades", new GradeRequest(second.Id, null), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/assessments/{assessment.Id}")).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<List<AssessmentDto>>($"/api/assessments?groupId={group.Id}", JsonOptions))!);
    }

    [Fact]
    public async Task AnotherTeachersAssessmentIsNotAccessible()
    {
        await AuthenticateAsync();
        var group = await PostAsync<GroupDto>("/api/groups", new CreateGroupRequest("Чужая группа"));
        var temporary = await PostAsync<TemporaryPasswordDto>("/api/admin/teachers", new SaveTeacherRequest("second", "Второй преподаватель", false, true));
        using var other = factory.CreateClient();
        var token = await LoginAsync(other, "second", temporary.Password);
        other.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        var date = DateOnly.FromDateTime(DateTime.Today).AddDays(-7);
        var slot = await other.PostAsJsonAsync("/api/schedule/slots", new SaveSlotRequest(group.Id, null, "Физика", null, date.DayOfWeek, new TimeOnly(4, 0), new TimeOnly(5, 0), WeekParity.Every, date), JsonOptions);
        Assert.Equal(HttpStatusCode.OK, slot.StatusCode);
        var created = await other.PostAsJsonAsync("/api/assessments", new SaveAssessmentRequest(group.Id, "Физика", "Тест", AssessmentKind.Test, date), JsonOptions);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var assessment = await created.Content.ReadFromJsonAsync<AssessmentDto>(JsonOptions);
        Assert.DoesNotContain((await client.GetFromJsonAsync<List<AssessmentDto>>("/api/assessments", JsonOptions))!, x => x.Id == assessment!.Id);
        var update = await client.PutAsJsonAsync($"/api/assessments/{assessment!.Id}", new SaveAssessmentRequest(group.Id, "Физика", "Чужой тест", AssessmentKind.Test, date), JsonOptions);
        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/api/assessments/{assessment.Id}/grades", new GradeRequest(999999, 50), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/assessments/{assessment.Id}")).StatusCode);
    }

    private async Task<T> PostAsync<T>(string path, object value)
    {
        var response = await client.PostAsJsonAsync(path, value, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<T>(JsonOptions);
        return result!;
    }

    private async Task<(GroupDto Group, StudentDto Student, SlotDto Slot, DateOnly Date)> LessonAsync()
    {
        var group = await PostAsync<GroupDto>("/api/groups", new CreateGroupRequest("Группа оценок"));
        var student = await PostAsync<StudentDto>("/api/students", new SaveStudentRequest("Первый", group.Id));
        var date = DateOnly.FromDateTime(DateTime.Today).AddDays(-7);
        var slot = await PostAsync<SlotDto>("/api/schedule/slots", new SaveSlotRequest(group.Id, null, "Алгебра", null, date.DayOfWeek, new TimeOnly(2, 0), new TimeOnly(3, 0), WeekParity.Every, date));
        return (group, student, slot, date);
    }

    private async Task AuthenticateAsync()
    {
        var token = await LoginAsync(client, "teacher", "teacher123");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
    }

    private static async Task<TokenResponse> LoginAsync(HttpClient target, string login, string password)
    {
        var response = await target.PostAsJsonAsync("/api/auth/login", new LoginRequest(login, password), JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(JsonOptions);
        Assert.NotNull(token);
        return token;
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

    private sealed class GradeFactory(string directory) : WebApplicationFactory<Program>
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
