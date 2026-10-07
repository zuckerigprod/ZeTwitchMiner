using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ZeTwitchMiner.Core;

namespace ZeTwitchMiner.Twitch;

public sealed record DeviceCode(string UserCode, string VerificationUri, DateTimeOffset ExpiresAt);

public sealed class SessionData
{
    public string? Client { get; set; }
    public string? DeviceId { get; set; }
    public string? AccessToken { get; set; }
    public long UserId { get; set; }
}

public sealed class TwitchAuth(TwitchHttp http)
{
    private const string IdUrl = "https://id.twitch.tv/oauth2";
    private readonly SemaphoreSlim _lock = new(1, 1);
    private TaskCompletionSource _browserRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _validated;

    private TwitchConfig Config => http.Config;
    public ClientProfile Profile => http.Profile;

    public string SessionId { get; } = RandomHex(16);
    public string? DeviceId { get; private set; }
    public string? AccessToken { get; private set; }
    public long UserId { get; private set; }
    public bool IsLoggedIn => AccessToken is not null && UserId != 0 && _validated;

    // null значит, что код больше не нужен (вход завершён)
    public event Action<DeviceCode?>? CodeRequired;
    public event Action<bool>? BrowserLoginActive;

    // Сохранённая сессия определяет, каким клиентом ходить в API
    public static SessionData? ReadSession()
    {
        try
        {
            if (!File.Exists(AppPaths.Session)) return null;
            var raw = ProtectedData.Unprotect(File.ReadAllBytes(AppPaths.Session), null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize(raw, AppJson.Default.SessionData);
        }
        catch (Exception ex)
        {
            // Сессия с другого ПК не расшифруется, это нормально
            Log.Warn("Session not restored: " + ex.Message);
            return null;
        }
    }

    public void Restore(SessionData? data)
    {
        if (data is null || data.Client != Profile.Key) return;
        DeviceId = data.DeviceId;
        AccessToken = data.AccessToken;
        UserId = data.UserId;
        AddDeviceCookie();
    }

    private void AddDeviceCookie()
    {
        if (DeviceId is not null)
            http.Cookies.Add(new Cookie("unique_id", DeviceId, "/", ".twitch.tv"));
    }

    private void SaveSession()
    {
        try
        {
            var data = new SessionData { Client = Profile.Key, DeviceId = DeviceId, AccessToken = AccessToken, UserId = UserId };
            var raw = JsonSerializer.SerializeToUtf8Bytes(data, AppJson.Default.SessionData);
            File.WriteAllBytes(AppPaths.Session, ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save session: " + ex.Message);
        }
    }

    // Следующий запрос перепроверит токен
    public void Invalidate() => _validated = false;

    // Сессия бесполезна: забываем токен, при перезапуске снова покажем вход
    public void Reject()
    {
        AccessToken = null;
        UserId = 0;
        _validated = false;
        try { File.Delete(AppPaths.Session); } catch { }
    }

    public void RequestBrowserLogin() => _browserRequest.TrySetResult();

    public async Task EnsureAsync(CancellationToken ct)
    {
        if (IsLoggedIn) return;
        await _lock.WaitAsync(ct);
        try
        {
            if (!IsLoggedIn) await ValidateAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task ValidateAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (AccessToken is null) await LoginAsync(ct);

            using var response = await http.SendAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, IdUrl + "/validate");
                req.Headers.Authorization = new AuthenticationHeaderValue("OAuth", AccessToken);
                return req;
            }, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                Log.Info("Saved session is no longer valid, logging in again");
                AccessToken = null;
                continue;
            }
            if (response.StatusCode != HttpStatusCode.OK) continue;

            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
            var clientId = json.Str("client_id");
            if (Config.Clients.Values.FirstOrDefault(c => c.Id == clientId) is not { } profile)
            {
                Log.Warn($"Token belongs to an unknown client id {clientId}, logging in again");
                AccessToken = null;
                continue;
            }

            http.Profile = profile;
            UserId = long.Parse(json.Str("user_id"));
            await EnsureDeviceIdAsync(ct);
            _validated = true;
            SaveSession();
            CodeRequired?.Invoke(null);
            Log.Info($"Logged in as user {UserId} via '{profile.Key}' client");
            return;
        }
        throw new MinerException("Login verification failed");
    }

    private async Task EnsureDeviceIdAsync(CancellationToken ct)
    {
        if (DeviceId is not null) return;
        using var _ = await http.SendAsync(() => WithHeaders(new HttpRequestMessage(HttpMethod.Get, Profile.Url)), ct);
        DeviceId = http.Cookies.GetCookies(new Uri(Profile.Url))["unique_id"]?.Value ?? RandomHex(32);
        AddDeviceCookie();
    }

    // Код активации по очереди у клиентов из loginClients, параллельно ждём нажатия "Войти через браузер"
    private async Task LoginAsync(CancellationToken ct)
    {
        while (true)
        {
            _browserRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var deviceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var device = DeviceLoginAsync(deviceCts.Token);

            var done = await Task.WhenAny(device, _browserRequest.Task);
            if (done == device)
            {
                await device;
                return;
            }

            deviceCts.Cancel();
            try { await device; } catch (OperationCanceledException) { }

            CodeRequired?.Invoke(null);
            BrowserLoginActive?.Invoke(true);
            try
            {
                var session = await BrowserLogin.RunAsync(ct);
                if (session is not null)
                {
                    http.Profile = Config.Client(TwitchConfig.WebClient);
                    AccessToken = session.AuthToken;
                    DeviceId = session.DeviceId;
                    AddDeviceCookie();
                    return;
                }
                Log.Info("Browser closed before login finished");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error("Browser login failed: " + ex.Message);
            }
            finally
            {
                BrowserLoginActive?.Invoke(false);
            }
        }
    }

    private async Task DeviceLoginAsync(CancellationToken ct)
    {
        while (true)
        {
            var (profile, code) = await RequestCodeAsync(ct);
            http.Profile = profile;
            DeviceId = null;

            var deviceCode = code.Str("device_code");
            var interval = TimeSpan.FromSeconds(Math.Max(code.Int("interval"), 1));
            var expiresAt = DateTimeOffset.UtcNow.AddSeconds(code.Int("expires_in") is > 0 and var e ? e : 1800);
            CodeRequired?.Invoke(new DeviceCode(code.Str("user_code"), code.Str("verification_uri"), expiresAt));

            try
            {
                while (true)
                {
                    await Task.Delay(interval, ct);
                    using var tokenResponse = await http.SendAsync(() => DeviceRequest(profile, "/token", new()
                    {
                        ["client_id"] = profile.Id,
                        ["device_code"] = deviceCode,
                        ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    }), ct, expiresAt);

                    // 400 значит, что код ещё не ввели
                    if (tokenResponse.StatusCode != HttpStatusCode.OK) continue;
                    var token = JsonNode.Parse(await tokenResponse.Content.ReadAsStringAsync(ct))!;
                    AccessToken = token.Str("access_token");
                    return;
                }
            }
            catch (RequestInvalidException)
            {
                Log.Info("Activation code expired, requesting a new one");
            }
        }
    }

    private async Task<(ClientProfile, JsonNode)> RequestCodeAsync(CancellationToken ct)
    {
        foreach (var key in Config.LoginClients)
        {
            var profile = Config.Client(key);
            http.Profile = profile;
            using var response = await http.SendAsync(() => DeviceRequest(profile, "/device", new()
            {
                ["client_id"] = profile.Id,
                ["scopes"] = "",
            }), ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode && JsonNode.Parse(body) is { } json && json["device_code"] is not null)
                return (profile, json);
            Log.Warn($"Device login rejected for '{key}' client: {body}");
        }
        throw new MinerException(Loc.T("Login.DeviceUnavailable"));
    }

    public async Task LogoutAsync(CancellationToken ct)
    {
        if (AccessToken is { } token)
        {
            try
            {
                using var _ = await http.SendAsync(() => new HttpRequestMessage(HttpMethod.Post, IdUrl + "/revoke")
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["client_id"] = Profile.Id,
                        ["token"] = token,
                    }),
                }, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn("Token revoke failed: " + ex.Message);
            }
        }
        AccessToken = null;
        UserId = 0;
        _validated = false;
        try { File.Delete(AppPaths.Session); } catch { }
        BrowserLogin.ClearProfile();
    }

    private HttpRequestMessage DeviceRequest(ClientProfile profile, string path, Dictionary<string, string> form)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, IdUrl + path) { Content = new FormUrlEncodedContent(form) };
        var h = req.Headers;
        h.TryAddWithoutValidation("Accept", "application/json");
        h.TryAddWithoutValidation("Accept-Language", "en-US");
        h.TryAddWithoutValidation("Cache-Control", "no-cache");
        h.TryAddWithoutValidation("Pragma", "no-cache");
        h.TryAddWithoutValidation("Client-Id", profile.Id);
        h.TryAddWithoutValidation("Origin", profile.Url);
        h.TryAddWithoutValidation("Referer", profile.Url);
        if (DeviceId is not null) h.TryAddWithoutValidation("X-Device-Id", DeviceId);
        return req;
    }

    public HttpRequestMessage WithHeaders(HttpRequestMessage req, bool gql = false)
    {
        var h = req.Headers;
        h.TryAddWithoutValidation("Accept", "*/*");
        h.TryAddWithoutValidation("Accept-Language", "en-US");
        h.TryAddWithoutValidation("Pragma", "no-cache");
        h.TryAddWithoutValidation("Cache-Control", "no-cache");
        h.TryAddWithoutValidation("Client-Id", Profile.Id);
        h.TryAddWithoutValidation("Client-Session-Id", SessionId);
        if (DeviceId is not null) h.TryAddWithoutValidation("X-Device-Id", DeviceId);
        if (gql)
        {
            h.TryAddWithoutValidation("Origin", Profile.Url);
            h.TryAddWithoutValidation("Referer", Profile.Url);
            h.TryAddWithoutValidation("Authorization", "OAuth " + AccessToken);
        }
        return req;
    }

    public static string RandomHex(int length)
    {
        Span<byte> bytes = stackalloc byte[(length + 1) / 2];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexStringLower(bytes)[..length];
    }

    public static string RandomNonce(int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++) sb.Append(chars[RandomNumberGenerator.GetInt32(chars.Length)]);
        return sb.ToString();
    }
}
