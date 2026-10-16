using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using BiStudent.Contracts;

namespace BiStudent.Client.Tests;

public sealed class ApiClientTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static ApiClient Client(FakeServer server) => new(new ClientSettings { ServerUrl = "http://test/" }, server);
    private static string Body(object value) => JsonSerializer.Serialize(value, JsonOptions);

    [Theory]
    [InlineData("")]
    [InlineData("ftp://x")]
    [InlineData("не url")]
    public void InvalidServerUrl(string address) =>
        Assert.Throws<ApiException>(() => new ApiClient(new ClientSettings { ServerUrl = address }, new FakeServer()));

    [Fact]
    public async Task LoginSetsBearerAndRefreshToken()
    {
        var server = new FakeServer();
        server.On(HttpMethod.Post, "api/auth/login", _ => FakeServer.Json(new TokenResponse("access", 60, "refresh")));
        server.On(HttpMethod.Get, "api/groups", _ => FakeServer.Json(Array.Empty<GroupDto>()));
        using var api = Client(server);
        var changes = new List<string?>();
        api.RefreshTokenChanged += changes.Add;
        await api.LoginAsync("teacher", "secret", true);
        await api.GroupsAsync();
        Assert.Equal(Body(new LoginRequest("teacher", "secret", true)), server.Requests[0].Body);
        Assert.Null(server.Requests[0].Authorization);
        Assert.Equal("Bearer access", server.Requests[1].Authorization);
        Assert.Equal("refresh", api.RefreshToken);
        Assert.Equal(new string?[] { "refresh" }, changes);
    }

    [Fact]
    public async Task UnauthorizedPutRefreshesAndReplaysBody()
    {
        var server = new FakeServer();
        var attempts = 0;
        server.On(HttpMethod.Post, "api/auth/login", _ => FakeServer.Json(new TokenResponse("old", 60, "old-r")));
        server.On(HttpMethod.Post, "api/auth/refresh", _ => FakeServer.Json(new TokenResponse("new", 60, "new-r")));
        server.On(HttpMethod.Put, "api/lessons/2/2026-10-16/attendance", request =>
        {
            attempts++;
            return new HttpResponseMessage(request.Headers.Authorization?.Parameter == "old" ? HttpStatusCode.Unauthorized : HttpStatusCode.NoContent);
        });
        using var api = Client(server);
        var unauthorized = 0;
        api.Unauthorized += () => unauthorized++;
        await api.LoginAsync("teacher", "secret");
        await api.MarkAsync(2, new DateOnly(2026, 10, 16), 3, AttendanceStatus.Excused, ExcuseReason.Illness, "Справка", true);
        var requests = server.Requests;
        Assert.Equal(1, requests.Count(x => x.Path == "api/auth/refresh"));
        Assert.Equal(2, attempts);
        Assert.Equal("Bearer old", requests[1].Authorization);
        Assert.Equal("Bearer new", requests[3].Authorization);
        Assert.Equal(Body(new MarkRequest(3, AttendanceStatus.Excused, ExcuseReason.Illness, "Справка", true)), requests[1].Body);
        Assert.Equal(requests[1].Body, requests[3].Body);
        Assert.Equal(Body(new RefreshRequest("old-r")), requests[2].Body);
        Assert.Equal(0, unauthorized);
    }

    [Fact]
    public async Task ParallelUnauthorizedRequestsShareRefresh()
    {
        var server = new FakeServer();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = 0;
        var refreshes = 0;
        server.On(HttpMethod.Post, "api/auth/login", _ => FakeServer.Json(new TokenResponse("old", 60, "old-r")));
        server.On(HttpMethod.Post, "api/auth/refresh", async _ =>
        {
            Interlocked.Increment(ref refreshes);
            started.TrySetResult();
            await release.Task;
            return FakeServer.Json(new TokenResponse("new", 60, "new-r"));
        });
        Func<HttpRequestMessage, HttpResponseMessage> response = request =>
        {
            if (request.Headers.Authorization?.Parameter != "old") return FakeServer.Json(Array.Empty<object>());
            if (Interlocked.Increment(ref failed) == 2) bothFailed.TrySetResult();
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        };
        server.On(HttpMethod.Get, "api/groups", response);
        server.On(HttpMethod.Get, "api/groups/5/students", response);
        using var api = Client(server);
        await api.LoginAsync("teacher", "secret");
        var first = api.GroupsAsync();
        var second = api.StudentsAsync(5);
        await Task.WhenAll(started.Task, bothFailed.Task);
        release.TrySetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, refreshes);
    }

    [Fact]
    public async Task FailedRefreshRaisesUnauthorized()
    {
        var server = new FakeServer();
        server.On(HttpMethod.Post, "api/auth/login", _ => FakeServer.Json(new TokenResponse("old", 60, "old-r")));
        server.On(HttpMethod.Get, "api/groups", _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        server.On(HttpMethod.Post, "api/auth/refresh", _ => FakeServer.Error(HttpStatusCode.Unauthorized, "Истёк"));
        using var api = Client(server);
        var raised = 0;
        api.Unauthorized += () => raised++;
        await api.LoginAsync("teacher", "secret");
        var error = await Assert.ThrowsAsync<ApiException>(() => api.GroupsAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task EndSessionDiscardsPendingRefresh()
    {
        var server = new FakeServer();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.On(HttpMethod.Post, "api/auth/login", _ => FakeServer.Json(new TokenResponse("old", 60, "old-r")));
        server.On(HttpMethod.Get, "api/groups", _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        server.On(HttpMethod.Post, "api/auth/refresh", async _ =>
        {
            started.TrySetResult();
            await release.Task;
            return FakeServer.Json(new TokenResponse("new", 60, "new-r"));
        });
        using var api = Client(server);
        var changes = new List<string?>();
        api.RefreshTokenChanged += changes.Add;
        await api.LoginAsync("teacher", "secret");
        var pending = api.GroupsAsync();
        await started.Task;
        api.EndSession();
        release.TrySetResult();
        await Assert.ThrowsAsync<ApiException>(() => pending);
        Assert.DoesNotContain("new-r", changes);
        Assert.Null(api.RefreshToken);
    }

    [Fact]
    public async Task ResumeHandlesSuccessRejectionAndNetworkFailure()
    {
        var server = new FakeServer();
        server.On(HttpMethod.Post, "api/auth/refresh", _ => FakeServer.Json(new TokenResponse("access", 60, "next")));
        server.On(HttpMethod.Get, "api/me", _ => FakeServer.Json(new TeacherDto(7, "Преподаватель", "teacher")));
        using var api = Client(server);
        Assert.True(await api.TryResumeAsync("saved"));
        Assert.Equal(7, api.Teacher?.Id);
        Assert.Equal(Body(new RefreshRequest("saved")), server.Requests[0].Body);
        var rejected = new FakeServer();
        rejected.On(HttpMethod.Post, "api/auth/refresh", _ => FakeServer.Error(HttpStatusCode.Unauthorized, "Истёк"));
        using var rejectedApi = Client(rejected);
        Assert.False(await rejectedApi.TryResumeAsync("saved"));
        var offline = new FakeServer();
        offline.On(HttpMethod.Post, "api/auth/refresh", _ => Task.FromException<HttpResponseMessage>(new HttpRequestException("offline")));
        using var offlineApi = Client(offline);
        await Assert.ThrowsAsync<ApiException>(() => offlineApi.TryResumeAsync("saved"));
    }

    [Fact]
    public async Task ChangePasswordKeepsTokensWhenMeFails()
    {
        var server = new FakeServer();
        server.On(HttpMethod.Post, "api/auth/change-password", _ => FakeServer.Json(new TokenResponse("new", 60, "new-r")));
        server.On(HttpMethod.Get, "api/me", _ => FakeServer.Json(new TeacherDto(8, "Новый", "teacher")));
        using var api = Client(server);
        await api.ChangePasswordAsync("old", "new password");
        Assert.Equal("new-r", api.RefreshToken);
        Assert.Equal(8, api.Teacher?.Id);
        Assert.Equal(Body(new ChangePasswordRequest("old", "new password")), server.Requests[0].Body);
        var failing = new FakeServer();
        var meCalls = 0;
        failing.On(HttpMethod.Post, "api/auth/change-password", _ => FakeServer.Json(new TokenResponse("new", 60, "new-r")));
        failing.On(HttpMethod.Get, "api/me", _ => { meCalls++; return FakeServer.Error(HttpStatusCode.InternalServerError, "Ошибка"); });
        using var failingApi = Client(failing);
        await failingApi.ChangePasswordAsync("old", "new password");
        Assert.Equal(2, meCalls);
        Assert.Equal("new-r", failingApi.RefreshToken);
        var bad = new FakeServer();
        bad.On(HttpMethod.Post, "api/auth/change-password", _ => FakeServer.Error(HttpStatusCode.BadRequest, "Неверный пароль"));
        using var badApi = Client(bad);
        var error = await Assert.ThrowsAsync<ApiException>(() => badApi.ChangePasswordAsync("old", "new password"));
        Assert.Equal("Неверный пароль", error.Message);
    }

    [Fact]
    public async Task ServerErrorMessagesAreReadable()
    {
        var server = new FakeServer();
        server.On(HttpMethod.Get, "api/groups", _ => FakeServer.Error(HttpStatusCode.BadRequest, "Ошибка проверки"));
        using var api = Client(server);
        var error = await Assert.ThrowsAsync<ApiException>(() => api.GroupsAsync());
        Assert.Equal("Ошибка проверки", error.Message);
        var empty = new FakeServer();
        empty.On(HttpMethod.Get, "api/groups", _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("") });
        using var emptyApi = Client(empty);
        error = await Assert.ThrowsAsync<ApiException>(() => emptyApi.GroupsAsync());
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public async Task EnrollmentCodeAndAssessmentQueries()
    {
        var server = new FakeServer();
        var code = new EnrollmentCodeDto(2, "Группа", "ABC", DateTime.UtcNow, DateTime.UtcNow.AddHours(1), new List<string>());
        server.On(HttpMethod.Get, "api/groups/1/enrollment-code", _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        server.On(HttpMethod.Get, "api/groups/2/enrollment-code", _ => FakeServer.Json(code));
        server.On(HttpMethod.Get, "api/assessments", _ => FakeServer.Json(Array.Empty<AssessmentDto>()));
        using var api = Client(server);
        Assert.Null(await api.EnrollmentCodeAsync(1));
        Assert.Equal("ABC", (await api.EnrollmentCodeAsync(2))?.Code);
        await api.AssessmentsAsync(null, null);
        await api.AssessmentsAsync(5, "Базы данных");
        Assert.Equal("api/assessments", server.Requests[2].Path);
        Assert.Equal("api/assessments?groupId=5&subject=%D0%91%D0%B0%D0%B7%D1%8B%20%D0%B4%D0%B0%D0%BD%D0%BD%D1%8B%D1%85", server.Requests[3].Path);
    }

    [Fact]
    public async Task BinaryResponsesPreserveBytesAndJournalFileName()
    {
        var server = new FakeServer();
        server.On(HttpMethod.Get, "api/face-submissions/3/images/1", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2 }) });
        server.On(HttpMethod.Get, "api/admin/backups/backup%20one.zip", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 3, 4 }) });
        server.On(HttpMethod.Get, "api/journal/5/csv", _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 5, 6 }) };
            response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = "journal.csv" };
            return response;
        });
        using var api = Client(server);
        Assert.Equal(new byte[] { 1, 2 }, await api.FaceSubmissionImageAsync(3, 1));
        var backup = await api.DownloadBackupAsync("backup one.zip");
        Assert.Equal(new byte[] { 3, 4 }, backup.Content);
        Assert.Equal("backup one.zip", backup.FileName);
        var journal = await api.JournalFileAsync(5, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 16), null, "csv");
        Assert.Equal(new byte[] { 5, 6 }, journal.Content);
        Assert.Equal("journal.csv", journal.FileName);
    }

    [Fact]
    public async Task PublicRoutesUseExpectedMethodsPathsAndBodies()
    {
        foreach (var route in Routes())
        {
            var server = new FakeServer();
            server.On(route.Method, route.Path.Split('?')[0], _ => FakeServer.Json(route.Response));
            using var api = Client(server);
            await route.Call(api);
            var request = Assert.Single(server.Requests);
            Assert.Equal(route.Method, request.Method);
            Assert.Equal(route.Path, request.Path);
            Assert.Equal(route.Body, request.Body);
        }
    }

    private static IEnumerable<(HttpMethod Method, string Path, string? Body, object Response, Func<ApiClient, Task> Call)> Routes()
    {
        var date = new DateOnly(2026, 10, 16);
        var slot = new SaveSlotRequest(5, null, "Математика", "101", DayOfWeek.Friday, new TimeOnly(9, 0), new TimeOnly(10, 30), WeekParity.Odd, null);
        var teacher = new SaveTeacherRequest("teacher", "Преподаватель", true, true);
        var assessment = new SaveAssessmentRequest(5, "Математика", "Тест", AssessmentKind.Test, date);
        yield return (HttpMethod.Get, "api/settings/automation", null, new { }, api => api.AutomationAsync());
        yield return (HttpMethod.Get, "api/recognition/status", null, new { }, api => api.RecognitionStatusAsync());
        yield return (HttpMethod.Get, "api/admin/backups", null, Array.Empty<object>(), api => api.BackupsAsync());
        yield return (HttpMethod.Post, "api/admin/backups", null, new { }, api => api.CreateBackupAsync());
        yield return (HttpMethod.Post, "api/admin/restore", "ZIP", new { }, api => api.RestoreAsync("ZIP"u8.ToArray()));
        yield return (HttpMethod.Get, "api/admin/teachers", null, Array.Empty<object>(), api => api.TeachersAsync());
        yield return (HttpMethod.Post, "api/admin/teachers", Body(teacher), new { }, api => api.CreateTeacherAsync(teacher));
        yield return (HttpMethod.Put, "api/admin/teachers/7", Body(teacher), new { }, api => api.UpdateTeacherAsync(7, teacher));
        yield return (HttpMethod.Post, "api/admin/teachers/7/reset-password", null, new { }, api => api.ResetTeacherPasswordAsync(7));
        yield return (HttpMethod.Get, "api/dashboard", null, new { }, api => api.DashboardAsync());
        yield return (HttpMethod.Get, "api/report", null, new { }, api => api.ReportAsync());
        yield return (HttpMethod.Get, "api/schedule/day?date=2026-10-16", null, Array.Empty<object>(), api => api.DayAsync(date));
        yield return (HttpMethod.Get, "api/schedule/week?monday=2026-10-12", null, Array.Empty<object>(), api => api.WeekAsync(new DateOnly(2026, 10, 12)));
        yield return (HttpMethod.Get, "api/schedule/parity?date=2026-10-16", null, WeekParity.Odd, api => api.ParityAsync(date));
        yield return (HttpMethod.Post, "api/schedule/slots", Body(slot), new { }, api => api.SaveSlotAsync(null, slot));
        yield return (HttpMethod.Put, "api/schedule/slots/3", Body(slot), new { }, api => api.SaveSlotAsync(3, slot));
        yield return (HttpMethod.Delete, "api/schedule/slots/3", null, new { }, api => api.DeleteSlotAsync(3));
        yield return (HttpMethod.Post, "api/schedule/slots/3/cancel", Body(new CancelLessonRequest(date, "Причина")), new { }, api => api.CancelLessonAsync(3, date, "Причина"));
        yield return (HttpMethod.Post, "api/schedule/slots/3/restore", Body(new RestoreLessonRequest(date)), new { }, api => api.RestoreLessonAsync(3, date));
        yield return (HttpMethod.Post, "api/schedule/import", "a,b\n", new { }, api => api.ImportScheduleAsync("a,b\n"));
        yield return (HttpMethod.Get, "api/groups", null, Array.Empty<object>(), api => api.GroupsAsync());
        yield return (HttpMethod.Post, "api/groups", Body(new CreateGroupRequest("Группа")), new { }, api => api.CreateGroupAsync(new CreateGroupRequest("Группа")));
        yield return (HttpMethod.Delete, "api/groups/5", null, new { }, api => api.DeleteGroupAsync(5));
        yield return (HttpMethod.Get, "api/groups/5/students", null, Array.Empty<object>(), api => api.StudentsAsync(5));
        yield return (HttpMethod.Post, "api/students", Body(new SaveStudentRequest("Студент", 5)), new { }, api => api.SaveStudentAsync(null, new SaveStudentRequest("Студент", 5)));
        yield return (HttpMethod.Put, "api/students/8", Body(new SaveStudentRequest("Студент", 5)), new { }, api => api.SaveStudentAsync(8, new SaveStudentRequest("Студент", 5)));
        yield return (HttpMethod.Delete, "api/students/8", null, new { }, api => api.DeleteStudentAsync(8));
        yield return (HttpMethod.Post, "api/students/import", "name,group\n", new { }, api => api.ImportStudentsAsync("name,group\n"));
        yield return (HttpMethod.Post, "api/students/8/enroll", Body(new EnrollRequest(new List<byte[]> { new byte[] { 1, 2 } }, true)), new { }, api => api.EnrollAsync(8, new List<byte[]> { new byte[] { 1, 2 } }, true));
        yield return (HttpMethod.Post, "api/groups/5/enrollment-code", Body(new CreateEnrollmentCodeRequest(12)), new { }, api => api.CreateEnrollmentCodeAsync(5, 12));
        yield return (HttpMethod.Delete, "api/groups/5/enrollment-code", null, new { }, api => api.RevokeEnrollmentCodeAsync(5));
        yield return (HttpMethod.Get, "api/face-submissions", null, Array.Empty<object>(), api => api.FaceSubmissionsAsync());
        yield return (HttpMethod.Post, "api/face-submissions/9/approve", null, new { }, api => api.ApproveSubmissionAsync(9));
        yield return (HttpMethod.Post, "api/face-submissions/9/reject", Body(new RejectSubmissionRequest("Причина")), new { }, api => api.RejectSubmissionAsync(9, "Причина"));
        yield return (HttpMethod.Get, "api/lessons/3/2026-10-16/attendance", null, Array.Empty<object>(), api => api.AttendanceAsync(3, date));
        yield return (HttpMethod.Put, "api/lessons/3/2026-10-16/attendance", Body(new MarkRequest(8, AttendanceStatus.Excused, ExcuseReason.Official, "Приказ", true)), new { }, api => api.MarkAsync(3, date, 8, AttendanceStatus.Excused, ExcuseReason.Official, "Приказ", true));
        yield return (HttpMethod.Put, "api/lessons/3/2026-10-16/grade", Body(new GradeRequest(8, 90)), new { }, api => api.LessonGradeAsync(3, date, 8, 90));
        yield return (HttpMethod.Post, "api/lessons/3/2026-10-16/finish", null, new { count = 1 }, api => api.FinishAsync(3, date));
        yield return (HttpMethod.Post, "api/lessons/3/2026-10-16/all-present", null, new { count = 1 }, api => api.AllPresentAsync(3, date));
        yield return (HttpMethod.Post, "api/recognize", Body(new RecognizeRequest(3, date, new byte[] { 1, 2 })), new { }, api => api.RecognizeAsync(3, date, new byte[] { 1, 2 }, CancellationToken.None));
        yield return (HttpMethod.Get, "api/journal/5/subjects", null, Array.Empty<object>(), api => api.SubjectsAsync(5));
        yield return (HttpMethod.Post, "api/assessments", Body(assessment), new { }, api => api.SaveAssessmentAsync(null, assessment));
        yield return (HttpMethod.Put, "api/assessments/4", Body(assessment), new { }, api => api.SaveAssessmentAsync(4, assessment));
        yield return (HttpMethod.Delete, "api/assessments/4", null, new { }, api => api.DeleteAssessmentAsync(4));
        yield return (HttpMethod.Put, "api/assessments/4/grades", Body(new GradeRequest(8, 75)), new { }, api => api.AssessmentGradeAsync(4, 8, 75));
        yield return (HttpMethod.Put, "api/journal/5/final", Body(new FinalGradeRequest("Математика", 8, 80)), new { }, api => api.FinalGradeAsync(5, "Математика", 8, 80));
        yield return (HttpMethod.Post, "api/journal/5/final/fill", Body(new FillFinalRequest("Математика")), new { count = 1 }, api => api.FillFinalAsync(5, "Математика"));
        yield return (HttpMethod.Get, "api/journal/5?from=2026-10-01&to=2026-10-16&subject=%D0%91%D0%B0%D0%B7%D1%8B%20%D0%B4%D0%B0%D0%BD%D0%BD%D1%8B%D1%85", null, new { }, api => api.JournalAsync(5, new DateOnly(2026, 10, 1), date, "Базы данных"));
    }
}
