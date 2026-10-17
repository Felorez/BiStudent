using System.Net;
using BiStudent.Contracts;

namespace BiStudent.Client.Tests;

public sealed class AuditClientTests
{
    [Fact]
    public async Task ChangePasswordRefreshesExpiredAccessTokenAndRetries()
    {
        var server = new FakeServer();
        var attempts = 0;
        server.On(HttpMethod.Post, "api/auth/login", _ => FakeServer.Json(new TokenResponse("old", 60, "old-r")));
        server.On(HttpMethod.Post, "api/auth/refresh", _ => FakeServer.Json(new TokenResponse("new", 60, "new-r")));
        server.On(HttpMethod.Post, "api/auth/change-password", _ =>
            ++attempts == 1
                ? FakeServer.Error(HttpStatusCode.Unauthorized, "Истёк")
                : FakeServer.Json(new TokenResponse("changed", 60, "changed-r")));
        server.On(HttpMethod.Get, "api/me", _ => FakeServer.Json(new TeacherDto(7, "Преподаватель", "teacher")));
        using var api = new ApiClient(new ClientSettings { ServerUrl = "http://test/" }, server);

        await api.LoginAsync("teacher", "secret");
        await api.ChangePasswordAsync("secret", "new password");

        var requests = server.Requests;
        var changes = requests.Where(request => request.Path == "api/auth/change-password").ToList();
        Assert.Equal(2, changes.Count);
        Assert.Equal("Bearer old", changes[0].Authorization);
        Assert.Equal("Bearer new", changes[1].Authorization);
        Assert.Equal(changes[0].Body, changes[1].Body);
        Assert.Single(requests, request => request.Path == "api/auth/refresh");
        Assert.Equal("changed-r", api.RefreshToken);
    }
}
