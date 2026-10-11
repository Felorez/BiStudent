using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BiStudent.Contracts;

namespace BiStudent;

internal sealed class ApiException : Exception
{
    public HttpStatusCode? StatusCode { get; }

    public ApiException(HttpStatusCode? statusCode, string message, Exception? inner = null) : base(message, inner)
    {
        StatusCode = statusCode;
    }
}

internal sealed class ApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(60);
    private readonly HttpClient _http;
    private readonly SynchronizationContext? _uiContext;
    private string? _accessToken;
    private int _unauthorizedRaised;

    public TeacherDto? Teacher { get; private set; }
    public event Action? Unauthorized;

    public ApiClient() : this(ClientSettings.Load()) { }

    public ApiClient(ClientSettings settings)
    {
        var address = settings.ServerUrl?.Trim();
        if (string.IsNullOrWhiteSpace(address) || !Uri.TryCreate(address.TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ApiException(null, "Неверный адрес сервера.");
        _http = new HttpClient { BaseAddress = uri, Timeout = Timeout.InfiniteTimeSpan };
        _uiContext = SynchronizationContext.Current;
    }

    public async Task LoginAsync(string login, string password)
    {
        var token = await RequestAsync<TokenResponse>(HttpMethod.Post, "api/auth/login", Json(new LoginRequest(login, password)));
        _accessToken = token.AccessToken;
        _unauthorizedRaised = 0;
        Teacher = null;
    }

    public async Task<TeacherDto> LoadMeAsync()
    {
        Teacher = await GetAsync<TeacherDto>("api/me");
        return Teacher;
    }

    public Task<AutomationSettingsDto> AutomationAsync() => GetAsync<AutomationSettingsDto>("api/settings/automation");
    public Task<EnrollResultDto> RecognitionStatusAsync() => GetAsync<EnrollResultDto>("api/recognition/status");
    public Task<RecognitionSettingsDto> RecognitionSettingsAsync() => GetAsync<RecognitionSettingsDto>("api/settings/recognition");
    public Task<RecognitionSettingsDto> SaveRecognitionSettingsAsync(RecognitionSettingsDto settings) =>
        RequestAsync<RecognitionSettingsDto>(HttpMethod.Put, "api/settings/recognition", Json(settings));
    public Task<DashboardDto> DashboardAsync(CancellationToken cancellationToken = default) =>
        RequestAsync<DashboardDto>(HttpMethod.Get, "api/dashboard", cancellationToken: cancellationToken);
    public Task<ReportDto> ReportAsync() => GetAsync<ReportDto>("api/report");
    public Task<List<LessonDto>> DayAsync(DateOnly date, CancellationToken cancellationToken = default) =>
        RequestAsync<List<LessonDto>>(HttpMethod.Get, $"api/schedule/day?date={QueryDate(date)}", cancellationToken: cancellationToken);
    public Task<List<LessonDto>> WeekAsync(DateOnly monday) => GetAsync<List<LessonDto>>($"api/schedule/week?monday={QueryDate(monday)}");
    public Task<WeekParity> ParityAsync(DateOnly date) => GetAsync<WeekParity>($"api/schedule/parity?date={QueryDate(date)}");

    public Task<SlotDto> SaveSlotAsync(int? id, SaveSlotRequest request) =>
        RequestAsync<SlotDto>(id.HasValue ? HttpMethod.Put : HttpMethod.Post, id.HasValue ? $"api/schedule/slots/{id.Value}" : "api/schedule/slots", Json(request));
    public Task DeleteSlotAsync(int id) => RequestAsync(HttpMethod.Delete, $"api/schedule/slots/{id}");
    public Task CancelLessonAsync(int slotId, DateOnly date, string? reason) =>
        RequestAsync(HttpMethod.Post, $"api/schedule/slots/{slotId}/cancel", Json(new CancelLessonRequest(date, reason)));
    public Task RestoreLessonAsync(int slotId, DateOnly date) =>
        RequestAsync(HttpMethod.Post, $"api/schedule/slots/{slotId}/restore", Json(new RestoreLessonRequest(date)));
    public Task<ImportResultDto> ImportScheduleAsync(string csv) =>
        RequestAsync<ImportResultDto>(HttpMethod.Post, "api/schedule/import", new StringContent(csv, Encoding.UTF8, "text/csv"));

    public Task<List<GroupDto>> GroupsAsync() => GetAsync<List<GroupDto>>("api/groups");
    public Task<GroupDto> CreateGroupAsync(CreateGroupRequest request) => RequestAsync<GroupDto>(HttpMethod.Post, "api/groups", Json(request));
    public Task DeleteGroupAsync(int id) => RequestAsync(HttpMethod.Delete, $"api/groups/{id}");
    public Task<List<StudentDto>> StudentsAsync(int groupId) => GetAsync<List<StudentDto>>($"api/groups/{groupId}/students");
    public Task<StudentDto> SaveStudentAsync(int? id, SaveStudentRequest request) =>
        RequestAsync<StudentDto>(id.HasValue ? HttpMethod.Put : HttpMethod.Post, id.HasValue ? $"api/students/{id.Value}" : "api/students", Json(request));
    public Task DeleteStudentAsync(int id) => RequestAsync(HttpMethod.Delete, $"api/students/{id}");
    public Task<ImportResultDto> ImportStudentsAsync(string csv) =>
        RequestAsync<ImportResultDto>(HttpMethod.Post, "api/students/import", new StringContent(csv, Encoding.UTF8, "text/csv"));
    public Task<EnrollResultDto> EnrollAsync(int studentId, List<byte[]> images, bool allowSimilar = false) =>
        RequestAsync<EnrollResultDto>(HttpMethod.Post, $"api/students/{studentId}/enroll", Json(new EnrollRequest(images, allowSimilar)), timeout: LongTimeout);

    public Task<List<AttendanceRowDto>> AttendanceAsync(int slotId, DateOnly date, CancellationToken cancellationToken = default) =>
        RequestAsync<List<AttendanceRowDto>>(HttpMethod.Get, $"api/lessons/{slotId}/{QueryDate(date)}/attendance", cancellationToken: cancellationToken);
    public Task MarkAsync(int slotId, DateOnly date, int studentId, AttendanceStatus? status) =>
        RequestAsync(HttpMethod.Put, $"api/lessons/{slotId}/{QueryDate(date)}/attendance", Json(new MarkRequest(studentId, status)));
    public Task<CountResult> FinishAsync(int slotId, DateOnly date) =>
        RequestAsync<CountResult>(HttpMethod.Post, $"api/lessons/{slotId}/{QueryDate(date)}/finish");
    public Task<CountResult> AllPresentAsync(int slotId, DateOnly date) =>
        RequestAsync<CountResult>(HttpMethod.Post, $"api/lessons/{slotId}/{QueryDate(date)}/all-present");
    public Task<RecognitionResultDto> RecognizeAsync(int slotId, DateOnly date, byte[] jpeg, CancellationToken cancellationToken) =>
        RequestAsync<RecognitionResultDto>(HttpMethod.Post, "api/recognize", Json(new RecognizeRequest(slotId, date, jpeg)), cancellationToken);

    public Task<JournalDto> JournalAsync(int groupId, DateOnly from, DateOnly to, string? subject, CancellationToken cancellationToken = default) =>
        RequestAsync<JournalDto>(HttpMethod.Get, JournalPath(groupId, from, to, subject, ""), cancellationToken: cancellationToken, timeout: LongTimeout);

    public async Task<(byte[] Content, string FileName)> JournalFileAsync(int groupId, DateOnly from, DateOnly to, string? subject, string format)
    {
        if (format is not ("xlsx" or "csv")) throw new ArgumentException("Формат должен быть xlsx или csv.", nameof(format));
        using var deadline = new CancellationTokenSource(LongTimeout);
        using var response = await SendWithDeadlineAsync(HttpMethod.Get, JournalPath(groupId, from, to, subject, $"/{format}"), null, deadline.Token, CancellationToken.None);
        try
        {
            var content = await response.Content.ReadAsByteArrayAsync(deadline.Token);
            var name = response.Content.Headers.ContentDisposition?.FileNameStar
                ?? response.Content.Headers.ContentDisposition?.FileName;
            name = string.IsNullOrWhiteSpace(name) ? "" : Path.GetFileName(name.Trim('"'));
            if (string.IsNullOrWhiteSpace(name))
                name = $"journal-{groupId}-{from.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}-{to.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.{format}";
            return (content, name);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException) { throw NetworkError(ex); }
        catch (OperationCanceledException ex) { throw TimeoutError(ex); }
    }

    private static string QueryDate(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string JournalPath(int groupId, DateOnly from, DateOnly to, string? subject, string suffix)
    {
        var path = $"api/journal/{groupId}{suffix}?from={QueryDate(from)}&to={QueryDate(to)}";
        return string.IsNullOrWhiteSpace(subject) ? path : $"{path}&subject={Uri.EscapeDataString(subject)}";
    }

    private static HttpContent Json<T>(T value) =>
        new StringContent(JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, "application/json");

    private Task<T> GetAsync<T>(string path) => RequestAsync<T>(HttpMethod.Get, path);

    private async Task<T> RequestAsync<T>(HttpMethod method, string path, HttpContent? content = null, CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? DefaultTimeout);
        using var response = await SendWithDeadlineAsync(method, path, content, deadline.Token, cancellationToken);
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, deadline.Token)
                ?? throw new ApiException(response.StatusCode, "Пустой ответ сервера.");
        }
        catch (JsonException ex) { throw new ApiException(response.StatusCode, "Некорректный ответ сервера.", ex); }
        catch (Exception ex) when (ex is HttpRequestException or IOException) { throw NetworkError(ex); }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested) { throw TimeoutError(ex); }
    }

    private async Task RequestAsync(HttpMethod method, string path, HttpContent? content = null)
    {
        using var deadline = new CancellationTokenSource(DefaultTimeout);
        using var response = await SendWithDeadlineAsync(method, path, content, deadline.Token, CancellationToken.None);
    }

    private async Task<HttpResponseMessage> SendWithDeadlineAsync(HttpMethod method, string path, HttpContent? content,
        CancellationToken deadline, CancellationToken external)
    {
        try { return await SendAsync(method, path, content, deadline); }
        catch (OperationCanceledException ex) when (!external.IsCancellationRequested) { throw TimeoutError(ex); }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content = null, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        if (_accessToken is not null && path != "api/auth/login")
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        try
        {
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode) return response;
            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized && _accessToken is not null && path != "api/auth/login")
                {
                    RaiseUnauthorized();
                    throw new ApiException(response.StatusCode, "Сессия истекла — войдите снова.");
                }
                throw new ApiException(response.StatusCode, await ErrorMessageAsync(response, cancellationToken));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException) { throw NetworkError(ex); }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested) { throw TimeoutError(ex); }
    }

    private static async Task<string> ErrorMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
        if (!string.IsNullOrEmpty(body))
        {
            try
            {
                var error = JsonSerializer.Deserialize<ErrorDto>(body, JsonOptions);
                if (!string.IsNullOrWhiteSpace(error?.Message)) return error.Message;
            }
            catch (JsonException) { }
            return body;
        }
        return $"Ошибка сервера ({(int)response.StatusCode}).";
    }

    private ApiException NetworkError(Exception ex) =>
        new(null, $"Сервер недоступен ({_http.BaseAddress!.ToString().TrimEnd('/')}): {ex.Message}", ex);

    private ApiException TimeoutError(Exception ex) =>
        new(null, $"Сервер недоступен ({_http.BaseAddress!.ToString().TrimEnd('/')}): превышено время ожидания.", ex);

    private void RaiseUnauthorized()
    {
        if (Interlocked.Exchange(ref _unauthorizedRaised, 1) != 0) return;
        if (_uiContext is null || _uiContext == SynchronizationContext.Current) Unauthorized?.Invoke();
        else _uiContext.Post(_ => Unauthorized?.Invoke(), null);
    }

    public void Dispose() => _http.Dispose();
}
