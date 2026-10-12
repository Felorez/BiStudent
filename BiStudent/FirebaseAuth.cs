using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace BiStudent;

internal sealed record TeacherSession(string Uid, string Email, string Name, string IdToken = "", string RefreshToken = "", DateTimeOffset ExpiresAt = default)
{
    public override string ToString() => Name + " · " + Email;
}

internal sealed class FirebaseSettings
{
    public string ApiKey { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string TeacherEmail { get; set; } = "";
    public string TeacherUid { get; set; } = "";
    public string PreviousTeacherUid { get; set; } = "";
    public string TeacherName { get; set; } = "Ниязова Мадина Ержанкызы";
    public bool UseCloudData { get; set; }
    public string GroupId { get; set; } = "23-po-103-4r-2";
    public string GroupName { get; set; } = "23-ПО-103-4р-2";

    public static FirebaseSettings Load() => JsonSerializer.Deserialize<FirebaseSettings>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "firebase.json")),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidOperationException("Не найден конфигурационный файл Firebase.");
}

internal sealed class FirebaseAuth(FirebaseSettings settings, HttpClient http)
{
    public async Task<TeacherSession> RestoreAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync("https://securetoken.googleapis.com/v1/token?key=" + Uri.EscapeDataString(settings.ApiKey), new FormUrlEncodedContent(new Dictionary<string,string> { ["grant_type"]="refresh_token", ["refresh_token"]=refreshToken }),cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Сохранённый вход завершён. Введите пароль заново.");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken)); var root = json.RootElement;
        if (root.GetProperty("user_id").GetString() != settings.TeacherUid) throw new InvalidOperationException("У сохранённой учётной записи нет доступа.");
        return new TeacherSession(settings.TeacherUid,settings.TeacherEmail,settings.TeacherName,root.GetProperty("id_token").GetString()!,root.GetProperty("refresh_token").GetString()!,DateTimeOffset.UtcNow.AddSeconds(int.Parse(root.GetProperty("expires_in").GetString()!)));
    }
    public async Task<TeacherSession> SignInAsync(string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settings.TeacherUid) || string.IsNullOrWhiteSpace(settings.ApiKey))
            throw new InvalidOperationException("Учётная запись преподавателя ещё не настроена.");
        if (string.IsNullOrEmpty(password)) throw new InvalidOperationException("Введите пароль.");
        using var response = await http.PostAsJsonAsync(
            "https://identitytoolkit.googleapis.com/v1/accounts:signInWithPassword?key=" + Uri.EscapeDataString(settings.ApiKey),
            new { email = settings.TeacherEmail, password, returnSecureToken = true }, cancellationToken).ConfigureAwait(false);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        if (!response.IsSuccessStatusCode)
        {
            var code = json.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var message)
                ? message.GetString() ?? "" : "";
            throw new InvalidOperationException(code switch
            {
                "INVALID_LOGIN_CREDENTIALS" or "INVALID_PASSWORD" or "EMAIL_NOT_FOUND" => "Неверный пароль.",
                "USER_DISABLED" => "Учётная запись отключена в Firebase.",
                "OPERATION_NOT_ALLOWED" or "CONFIGURATION_NOT_FOUND" => "В Firebase нужно включить вход Email/Password.",
                "TOO_MANY_ATTEMPTS_TRY_LATER" => "Слишком много попыток. Попробуйте позже.",
                _ => "Firebase не смог выполнить вход. Проверьте настройки проекта."
            });
        }
        var uid = json.RootElement.GetProperty("localId").GetString();
        var email = json.RootElement.GetProperty("email").GetString();
        if (uid != settings.TeacherUid || !string.Equals(email, settings.TeacherEmail, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("У этой учётной записи нет доступа к BiStudent.");
        var root = json.RootElement;
        return new TeacherSession(uid!, email!, settings.TeacherName,
            root.TryGetProperty("idToken", out var token) ? token.GetString() ?? "" : "",
            root.TryGetProperty("refreshToken", out var refresh) ? refresh.GetString() ?? "" : "",
            DateTimeOffset.UtcNow.AddSeconds(root.TryGetProperty("expiresIn", out var expires) && int.TryParse(expires.GetString(), out var seconds) ? seconds : 3600));
    }
}
