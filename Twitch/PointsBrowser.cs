using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using ZeTwitchMiner.Core;

namespace ZeTwitchMiner.Twitch;

// Браузер без окна с одним только плеером Twitch. Баллы канала Twitch начисляет
// лишь настоящему веб-плееру, поэтому без браузера тут не обойтись.
// Работает на той же сессии, что и майнер: токен подкладываем в cookie чистого профиля.
public sealed class PointsBrowser : IDisposable
{
    private Process? _process;
    private ClientWebSocket? _page;
    private int _messageId;

    public string? Channel { get; private set; }
    // Смотрим на связь со страницей, а не на процесс: запущенный нами процесс мог передать работу другому
    public bool IsRunning => _page is { State: WebSocketState.Open };

    private static string ProfileDir => Path.Combine(AppPaths.DataDir, "points-browser");

    private static string PlayerUrl(string login) =>
        $"https://player.twitch.tv/?channel={Uri.EscapeDataString(login)}&parent=twitch.tv&muted=true&quality=160p30&autoplay=true";

    public async Task WatchAsync(string exe, string login, string token, string deviceId, CancellationToken ct)
    {
        if (IsRunning)
        {
            // Проверяем, что браузер ещё отвечает, иначе перезапускаем
            try { await CallAsync("Runtime.evaluate", new JsonObject { ["expression"] = "1" }, ct); }
            catch (Exception) when (!ct.IsCancellationRequested) { Stop(); }
        }
        if (!IsRunning)
        {
            Stop();
            await StartAsync(exe, token, deviceId, ct);
        }
        if (Channel == login) return;
        await CallAsync("Page.navigate", new JsonObject { ["url"] = PlayerUrl(login) }, ct);
        Channel = login;
        Log.Info("Points: watching " + login);
    }

    private async Task StartAsync(string exe, string token, string deviceId, CancellationToken ct)
    {
        // Каждый запуск с чистого профиля, чтобы не копились кеш и старые сессии
        KillOrphans();
        try { if (Directory.Exists(ProfileDir)) Directory.Delete(ProfileDir, true); } catch { }
        Directory.CreateDirectory(ProfileDir);

        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        if (!exe.Contains("headless-shell", StringComparison.OrdinalIgnoreCase)) psi.ArgumentList.Add("--headless=new");
        foreach (var arg in new[]
        {
            "--user-data-dir=" + ProfileDir, "--remote-debugging-port=0", "--mute-audio", "--no-first-run",
            "--no-default-browser-check", "--disable-gpu", "--window-size=320,180",
            "--autoplay-policy=no-user-gesture-required", "--renderer-process-limit=1", "--disable-extensions",
            "--disable-background-networking", "--disable-component-update", "--disable-sync",
            "--js-flags=--max-old-space-size=96", "about:blank",
        }) psi.ArgumentList.Add(arg);

        _process = Process.Start(psi) ?? throw new MinerException("Browser failed to start");
        ChildProcessJob.Attach(_process);
        var port = await WaitPortAsync(ct);

        using var http = new HttpClient();
        var targets = JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json", ct)) as JsonArray ?? [];
        var page = targets.FirstOrDefault(t => t?["type"]?.GetValue<string>() == "page")?["webSocketDebuggerUrl"]?.GetValue<string>()
            ?? throw new MinerException("Browser page not found");

        _page = new ClientWebSocket();
        await _page.ConnectAsync(new Uri(page), ct);
        await CallAsync("Network.enable", null, ct);
        await CallAsync("Network.setCookies", new JsonObject
        {
            ["cookies"] = new JsonArray(
                Cookie("auth-token", token),
                Cookie("unique_id", deviceId)),
        }, ct);
        Channel = null;
        Log.Info("Points browser started");
    }

    private static JsonObject Cookie(string name, string value) => new()
    {
        ["name"] = name, ["value"] = value, ["domain"] = ".twitch.tv", ["path"] = "/", ["secure"] = true,
    };

    private async Task<int> WaitPortAsync(CancellationToken ct)
    {
        var file = Path.Combine(ProfileDir, "DevToolsActivePort");
        for (var i = 0; i < 100; i++)
        {
            if (File.Exists(file))
            {
                try
                {
                    var first = (await File.ReadAllLinesAsync(file, ct)).FirstOrDefault();
                    if (int.TryParse(first, out var port)) return port;
                }
                catch (IOException)
                {
                    // Файл ещё пишется
                }
            }
            // Edge с неустановленным обновлением запускает новую версию и сразу выходит,
            // так что выход процесса ещё не ошибка: ждём порт от настоящего браузера
            if (_process is { HasExited: true } && i >= 75) throw new MinerException("Browser exited on start");
            await Task.Delay(200, ct);
        }
        throw new MinerException("Browser DevTools port not found");
    }

    private async Task CallAsync(string method, JsonObject? parameters, CancellationToken ct)
    {
        if (_page is null) throw new MinerException("Browser is not running");
        var id = ++_messageId;
        var msg = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() };
        await _page.SendAsync(Encoding.UTF8.GetBytes(msg.ToJsonString()), WebSocketMessageType.Text, true, ct);

        // Ждём ответ на свой id, события по пути пропускаем
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        while (true)
        {
            ms.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await _page.ReceiveAsync(buffer, timeout.Token);
                if (result.MessageType == WebSocketMessageType.Close) throw new MinerException("Browser closed the connection");
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            var node = JsonNode.Parse(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
            if (node?["id"] is JsonValue v && v.GetValue<int>() == id) return;
        }
    }

    public void Stop()
    {
        if (_process is null && _page is null) return;
        try { _page?.Dispose(); } catch { }
        _page = null;
        try
        {
            if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Log.Debug("Points browser kill failed: " + ex.Message);
        }
        _process?.Dispose();
        _process = null;
        // Добиваем то, что запустилось мимо нашего процесса
        KillOrphans();
        if (Channel is not null) Log.Info("Points browser stopped");
        Channel = null;
    }

    // Браузер мог остаться от прошлого запуска, если программу убили. Закрываем его.
    public static void KillOrphans()
    {
        var killed = 0;
        foreach (var name in new[] { "msedge", "chrome", "chrome-headless-shell" })
        foreach (var p in Process.GetProcessesByName(name))
        {
            using (p)
            {
                string? cmd = null;
                try { cmd = ProcessCommandLine.Get(p.Id); } catch { }
                if (cmd is null || !cmd.Contains(ProfileDir, StringComparison.OrdinalIgnoreCase)) continue;
                try { p.Kill(); killed++; } catch { }
            }
        }
        if (killed > 0) Log.Info($"Closed {killed} leftover browser processes");
    }

    public void Dispose() => Stop();
}
