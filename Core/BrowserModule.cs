using System.IO.Compression;
using System.Text.Json.Nodes;
using ZeTwitchMiner.Twitch;

namespace ZeTwitchMiner.Core;

// Браузер для сбора баллов: берём Edge или Chrome из системы,
// а если их нет, по кнопке скачиваем урезанный Chrome в папку программы
public static class BrowserModule
{
    private const string VersionsUrl = "https://googlechromelabs.github.io/chrome-for-testing/last-known-good-versions-with-downloads.json";
    private const string AllowedHost = "storage.googleapis.com";

    private static string ModuleDir => Path.Combine(AppPaths.DataDir, "browser-module");
    private static string ModuleExe => Path.Combine(ModuleDir, "chrome-headless-shell-win64", "chrome-headless-shell.exe");

    public static string? SystemBrowser => BrowserLogin.FindBrowser();
    public static bool ModuleInstalled => File.Exists(ModuleExe);

    // Скачанный браузер, если пользователь выбрал его, иначе системный, а без него опять же скачанный
    public static string? Resolve(bool preferModule) =>
        preferModule && ModuleInstalled ? ModuleExe
        : preferModule ? null
        : SystemBrowser ?? (ModuleInstalled ? ModuleExe : null);

    public static string? SystemBrowserName => SystemBrowser is { } path
        ? path.Contains("msedge", StringComparison.OrdinalIgnoreCase) ? "Microsoft Edge" : "Google Chrome"
        : null;

    public static async Task DownloadAsync(IProgress<double> progress, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        var versions = JsonNode.Parse(await http.GetStringAsync(VersionsUrl, ct))!;
        var url = (versions["channels"]?["Stable"]?["downloads"]?["chrome-headless-shell"] as JsonArray ?? [])
            .FirstOrDefault(e => e?["platform"]?.GetValue<string>() == "win64")?["url"]?.GetValue<string>()
            ?? throw new InvalidDataException("No win64 build in the version list");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != AllowedHost)
            throw new InvalidDataException("Unexpected download host: " + url);

        var tmp = Path.Combine(Path.GetTempPath(), "ztm-browser-module.zip");
        using (var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = File.Create(tmp);
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (total > 0) progress.Report((double)read / total);
            }
        }

        await Task.Run(() =>
        {
            if (Directory.Exists(ModuleDir)) Directory.Delete(ModuleDir, true);
            ZipFile.ExtractToDirectory(tmp, ModuleDir);
            File.Delete(tmp);
        }, ct);

        if (!ModuleInstalled) throw new InvalidDataException("Browser module is incomplete");
        Log.Info("Browser module installed");
    }

    public static void Remove()
    {
        try
        {
            if (Directory.Exists(ModuleDir)) Directory.Delete(ModuleDir, true);
        }
        catch (Exception ex)
        {
            Log.Warn("Browser module not removed: " + ex.Message);
        }
    }
}
