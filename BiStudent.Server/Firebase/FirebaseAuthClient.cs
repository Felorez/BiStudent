using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace BiStudent.Server.Firebase;

public sealed record FirebaseSession(
    string Uid, string Email, string IdToken, string RefreshToken, DateTimeOffset ExpiresAt)
{
    public bool ExpiresSoon(TimeProvider clock) =>
        ExpiresAt - clock.GetUtcNow() < TimeSpan.FromMinutes(5);
}

public sealed class FirebaseAuthClient(
    HttpClient http, IOptions<FirebaseOptions> options, TimeProvider? clock = null)
{
    private readonly FirebaseOptions settings = options.Value;
    private readonly TimeProvider time = clock ?? TimeProvider.System;

    public Task<FirebaseSession> SignInAsync(
        string email, string password, CancellationToken cancellationToken = default) =>
        AccountAsync("signInWithPassword", new { email, password, returnSecureToken = true },
            cancellationToken);

    public Task<FirebaseSession> SignUpAsync(
        string email, string password, CancellationToken cancellationToken = default) =>
        AccountAsync("signUp", new { email, password, returnSecureToken = true },
            cancellationToken);

    public async Task<FirebaseSession> RefreshAsync(
        string refreshToken, string email, CancellationToken cancellationToken = default)
    {
        var url = string.IsNullOrWhiteSpace(settings.AuthEmulatorHost)
            ? "https://securetoken.googleapis.com/v1/token"
            : $"http://{settings.AuthEmulatorHost}/securetoken.googleapis.com/v1/token";
        using var request = new HttpRequestMessage(HttpMethod.Post, WithKey(url))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken
            })
        };
        using var response = await SendAsync(request, cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = document.RootElement;
        return new FirebaseSession(
            root.GetProperty("user_id").GetString()!,
            email,
            root.GetProperty("id_token").GetString()!,
            root.GetProperty("refresh_token").GetString()!,
            Expiry(root.GetProperty("expires_in")));
    }

    public Task<FirebaseSession> ChangePasswordAsync(
        string idToken, string newPassword, CancellationToken cancellationToken = default) =>
        AccountAsync("update",
            new { idToken, password = newPassword, returnSecureToken = true }, cancellationToken);

    public async Task SendPasswordResetAsync(
        string email, CancellationToken cancellationToken = default)
    {
        using var request = AccountRequest("sendOobCode",
            new { email, requestType = "PASSWORD_RESET" });
        using var response = await SendAsync(request, cancellationToken);
    }

    public async Task DeleteAccountAsync(
        string idToken, CancellationToken cancellationToken = default)
    {
        using var request = AccountRequest("delete", new { idToken });
        using var response = await SendAsync(request, cancellationToken);
    }

    private async Task<FirebaseSession> AccountAsync(
        string method, object body, CancellationToken cancellationToken)
    {
        using var request = AccountRequest(method, body);
        using var response = await SendAsync(request, cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = document.RootElement;
        return new FirebaseSession(
            root.GetProperty("localId").GetString()!,
            root.GetProperty("email").GetString()!,
            root.GetProperty("idToken").GetString()!,
            root.GetProperty("refreshToken").GetString()!,
            Expiry(root.GetProperty("expiresIn")));
    }

    private HttpRequestMessage AccountRequest(string method, object body)
    {
        var url = string.IsNullOrWhiteSpace(settings.AuthEmulatorHost)
            ? $"https://identitytoolkit.googleapis.com/v1/accounts:{method}"
            : $"http://{settings.AuthEmulatorHost}/identitytoolkit.googleapis.com/v1/accounts:{method}";
        return new HttpRequestMessage(HttpMethod.Post, WithKey(url))
        {
            Content = JsonContent.Create(body)
        };
    }

    private string WithKey(string url) =>
        url + "?key=" + Uri.EscapeDataString(settings.ApiKey);

    private DateTimeOffset Expiry(JsonElement value)
    {
        var seconds = value.ValueKind == JsonValueKind.String
            ? long.Parse(value.GetString()!, System.Globalization.CultureInfo.InvariantCulture)
            : value.GetInt64();
        return time.GetUtcNow().AddSeconds(seconds);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.RequestTimeoutSeconds));
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, timeout.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new FirebaseUnavailableException("Firebase временно недоступен.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new FirebaseUnavailableException("Firebase временно недоступен.", exception);
        }

        if (response.IsSuccessStatusCode) return response;
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                (int)response.StatusCode >= 500)
                throw new FirebaseUnavailableException("Firebase временно недоступен.");

            string code;
            try
            {
                using var document = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync(cancellationToken));
                var message = document.RootElement.GetProperty("error")
                    .GetProperty("message").GetString() ?? "";
                code = message.Split([' ', ':'], 2)[0];
            }
            catch (JsonException)
            {
                code = "UNKNOWN_ERROR";
            }
            catch (KeyNotFoundException)
            {
                code = "UNKNOWN_ERROR";
            }
            throw new FirebaseAuthException(code);
        }
    }
}
