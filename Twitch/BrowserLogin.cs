using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using ZeTwitchMiner.Core;

namespace ZeTwitchMiner.Twitch;

public sealed record BrowserSession(string AuthToken, string? DeviceId);

// Вход через отдельный профиль Edge (или Chrome) в два шага.
// Сначала браузер запускается совсем обычным: защита Twitch при входе не должна видеть отладчик.
// Когда пользователь закрыл окно, тот же профиль на секунду поднимается без окна
// с DevTools, и из него читаются cookie auth-token и unique_id.
public static class BrowserLogin
{
    private static TaskCompletionSource? _finish;

    private static string ProfileDir => Path.Combine(AppPaths.DataDir, "browser");

    public static string? FindBrowser()
    {
        string?[] candidates =
        [
            Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe", "", null) as string,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe"),
            Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe", "", null) as string,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
        ];
        return candidates.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
    }

    // Кнопка "Готово" в программе: вежливо закрываем окно браузера, как будто его закрыл пользователь
    public static void Finish() => _finish?.TrySetResult();

    public static async Task<BrowserSession?> RunAsync(CancellationToken ct)
    {
        var exe = FindBrowser() ?? throw new MinerException(Loc.T("Login.NoBrowser"));
        Directory.CreateDirectory(ProfileDir);
        _finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Шаг 1: обычный браузер, без флагов автоматизации
        using (var login = Start(exe, "--app=https://www.twitch.tv/login", "--window-size=520,760"))
        {
            var closed = WaitProfileClosedAsync(ct);
            if (await Task.WhenAny(closed, _finish.Task) == _finish.Task)
            {
                CloseWindows();
                await closed.WaitAsync(TimeSpan.FromSeconds(15), ct).ContinueWith(_ => { }, CancellationToken.None);
                KillProfileProcesses();
            }
        }

        // Шаг 2: тот же профиль без окна, только чтобы прочитать cookie
        return await ReadCookiesAsync(exe, ct);
    }

    private static Process Start(string exe, params string[] extra)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        psi.ArgumentList.Add("--user-data-dir=" + ProfileDir);
        psi.ArgumentList.Add("--no-first-run");
        psi.ArgumentList.Add("--no-default-browser-check");
        psi.ArgumentList.Add("--disable-background-mode");
        foreach (var a in extra) psi.ArgumentList.Add(a);
        return Process.Start(psi) ?? throw new MinerException("Browser failed to start");
    }

    private static async Task<BrowserSession?> ReadCookiesAsync(string exe, CancellationToken ct)
    {
        var portFile = Path.Combine(ProfileDir, "DevToolsActivePort");
        try { File.Delete(portFile); } catch { }

        using var process = Start(exe, "--headless=new", "--remote-debugging-port=0", "about:blank");
        try
        {
            var endpoint = await WaitEndpointAsync(portFile, process, ct);
            if (endpoint is null) return null;

            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(endpoint, ct);
            var reply = await CallAsync(ws, 1, "Storage.getCookies", ct);
            var cookies = reply?["result"]?["cookies"] as JsonArray ?? [];

            string? Find(string name) => cookies
                .FirstOrDefault(c => c.Str("name") == name && c.Str("domain").EndsWith("twitch.tv", StringComparison.Ordinal))
                .Str("value") is { Length: > 0 } v ? v : null;

            try { await CallAsync(ws, 2, "Browser.close", ct); } catch { }
            return Find("auth-token") is { } token ? new BrowserSession(token, Find("unique_id")) : null;
        }
        finally
        {
            await Task.Delay(500, CancellationToken.None);
            KillProfileProcesses();
        }
    }

    // Выход из аккаунта стирает и профиль браузера, иначе он снова подставит старую сессию
    public static void ClearProfile()
    {
        try
        {
            KillProfileProcesses();
            if (Directory.Exists(ProfileDir)) Directory.Delete(ProfileDir, true);
        }
        catch (Exception ex)
        {
            Log.Debug("Browser profile not removed: " + ex.Message);
        }
    }

    // Процессы браузера, запущенные с нашим профилем. Lockfile держится, пока жив хоть один.
    private static async Task WaitProfileClosedAsync(CancellationToken ct)
    {
        var lockFile = Path.Combine(ProfileDir, "lockfile");
        await Task.Delay(3000, ct);
        while (true)
        {
            if (!IsLocked(lockFile)) return;
            await Task.Delay(1000, ct);
        }
    }

    private static bool IsLocked(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static IEnumerable<Process> ProfileProcesses()
    {
        foreach (var name in new[] { "msedge", "chrome" })
        foreach (var p in Process.GetProcessesByName(name))
        {
            string? cmd = null;
            try { cmd = ProcessCommandLine.Get(p.Id); } catch { }
            if (cmd is not null && cmd.Contains(ProfileDir, StringComparison.OrdinalIgnoreCase)) yield return p;
            else p.Dispose();
        }
    }

    private static void CloseWindows()
    {
        foreach (var p in ProfileProcesses())
            using (p)
                if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow();
    }

    private static void KillProfileProcesses()
    {
        foreach (var p in ProfileProcesses())
            using (p)
                try { p.Kill(); } catch { }
    }

    private static async Task<Uri?> WaitEndpointAsync(string portFile, Process process, CancellationToken ct)
    {
        for (var i = 0; i < 100; i++)
        {
            if (File.Exists(portFile))
            {
                try
                {
                    var lines = await File.ReadAllLinesAsync(portFile, ct);
                    if (lines.Length >= 2) return new Uri($"ws://127.0.0.1:{lines[0].Trim()}{lines[1].Trim()}");
                }
                catch (IOException)
                {
                    // Файл ещё пишется
                }
            }
            if (process.HasExited) return null;
            await Task.Delay(200, ct);
        }
        throw new MinerException("Browser DevTools endpoint not found");
    }

    private static async Task<JsonNode?> CallAsync(ClientWebSocket ws, int id, string method, CancellationToken ct)
    {
        var msg = new JsonObject { ["id"] = id, ["method"] = method }.ToJsonString();
        await ws.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, ct);

        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            ms.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            var node = JsonNode.Parse(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
            if (node?["id"] is JsonValue v && v.GetValue<int>() == id) return node;
        }
    }
}
