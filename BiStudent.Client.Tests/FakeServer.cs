using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace BiStudent.Client.Tests;
internal sealed class FakeServer : HttpMessageHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly object _gate = new();
    private readonly Dictionary<(HttpMethod, string), Func<HttpRequestMessage, Task<HttpResponseMessage>>> _routes = new();
    private readonly List<RequestEntry> _requests = new();
    internal sealed record RequestEntry(HttpMethod Method, string Path, string? Authorization, string? Body);
    public IReadOnlyList<RequestEntry> Requests { get { lock (_gate) return _requests.ToArray(); } }
    public void On(HttpMethod method, string path, Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        lock (_gate) _routes[(method, path)] = handler;
    }
    public void On(HttpMethod method, string path, Func<HttpRequestMessage, HttpResponseMessage> handler) =>
        On(method, path, request => Task.FromResult(handler(request)));
    public static HttpResponseMessage Json(object? value, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(JsonSerializer.Serialize(value, JsonOptions), System.Text.Encoding.UTF8, "application/json")
        };
    }
    public static HttpResponseMessage Error(HttpStatusCode statusCode, string message) => Json(new { message }, statusCode);
    public bool Handles(RequestEntry entry)
    {
        var path = entry.Path.Split('?')[0];
        lock (_gate) return _routes.ContainsKey((entry.Method, path));
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        var entry = new RequestEntry(request.Method, request.RequestUri.PathAndQuery.TrimStart('/'),
            request.Headers.Authorization?.ToString(), request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
        Func<HttpRequestMessage, Task<HttpResponseMessage>>? handler;
        lock (_gate)
        {
            _requests.Add(entry);
            _routes.TryGetValue((request.Method, path), out handler);
        }
        return handler is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : await handler(request);
    }
}
