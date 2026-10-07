using System.Net;
using System.Security.Authentication;
using ZeTwitchMiner.Core;

namespace ZeTwitchMiner.Twitch;

public class MinerException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class RequestInvalidException() : MinerException("Request is no longer valid");

public sealed class Backoff(double maximum, double baseValue = 2)
{
    public int Steps { get; private set; }

    public void Reset() => Steps = 0;

    public TimeSpan Next()
    {
        var value = Math.Pow(baseValue, Steps) * (0.9 + Random.Shared.NextDouble() * 0.2);
        if (value > maximum) return TimeSpan.FromSeconds(maximum);
        Steps++;
        return TimeSpan.FromSeconds(value);
    }
}

public sealed class TwitchHttp : IDisposable
{
    public CookieContainer Cookies { get; } = new();
    public HttpClient Client { get; }
    public TwitchConfig Config { get; }
    public TimeSpan TotalTimeout { get; }
    public IWebProxy? Proxy { get; }

    // Клиент, от имени которого идут запросы; меняется при входе
    public ClientProfile Profile { get; set; }

    // Сообщения для интерфейса вроде "Twitch недоступен, повтор через N секунд"
    public event Action<string>? Notice;

    public TwitchHttp(TwitchConfig config, ClientProfile profile, string proxy, int quality)
    {
        Config = config;
        Profile = profile;
        quality = Math.Clamp(quality, 1, 6);
        TotalTimeout = TimeSpan.FromSeconds(10 * quality);
        Proxy = BuildProxy(proxy);

        var handler = new SocketsHttpHandler
        {
            CookieContainer = Cookies,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(5 * quality),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 50,
            Proxy = Proxy,
            UseProxy = Proxy is not null,
        };
        Client = new HttpClient(new UserAgentHandler(this) { InnerHandler = handler }) { Timeout = TotalTimeout };
    }

    public static IWebProxy? BuildProxy(string proxy)
    {
        if (string.IsNullOrWhiteSpace(proxy) || !Uri.TryCreate(proxy.Trim(), UriKind.Absolute, out var uri)) return null;
        var webProxy = new WebProxy(new UriBuilder(uri) { UserName = "", Password = "" }.Uri);
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            webProxy.Credentials = new NetworkCredential(Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
        }
        return webProxy;
    }

    // Повторяет запрос, пока Twitch отвечает 5xx или нет сети. Любой ответ < 500 отдаётся вызывающему.
    public async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, CancellationToken ct, DateTimeOffset? invalidateAfter = null)
    {
        var backoff = new Backoff(180);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (invalidateAfter is { } limit && DateTimeOffset.UtcNow >= limit - TotalTimeout)
                throw new RequestInvalidException();

            var delay = backoff.Next();
            using var request = build();
            try
            {
                var response = await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
                if ((int)response.StatusCode < 500) return response;
                response.Dispose();
                Notice?.Invoke(Loc.F("Error.SiteDown", (int)Math.Round(delay.TotalSeconds)));
            }
            catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
            {
                if (backoff.Steps > 1)
                    Notice?.Invoke(Loc.F("Error.NoConnection", (int)Math.Round(delay.TotalSeconds), request.RequestUri?.Host));
            }
            await Task.Delay(delay, ct);
        }
    }

    public void Dispose() => Client.Dispose();

    private sealed class UserAgentHandler(TwitchHttp owner) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!request.Headers.Contains("User-Agent"))
                request.Headers.TryAddWithoutValidation("User-Agent", owner.Profile.UserAgent);
            return base.SendAsync(request, ct);
        }
    }
}
