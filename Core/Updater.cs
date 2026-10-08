using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ZeTwitchMiner.Core;

public sealed record UpdateInfo(Version Version, string Notes, string PageUrl, UpdateAsset? Installer, UpdateAsset? Portable);

public sealed record UpdateAsset(string Name, string Url, string Sha256, long Size);

// Проверяет релизы на GitHub и ставит новую версию: установщиком или заменой файлов в портативной папке
public sealed partial class Updater : ObservableObject
{
    private const string LatestUrl = "https://api.github.com/repos/zuckerigprod/ZeTwitchMiner/releases/latest";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly Func<Task> _exit;

    [ObservableProperty] private UpdateInfo? _available;
    [ObservableProperty] private bool _dismissed;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string? _message;

    public Updater(Func<Task> exit)
    {
        _exit = exit;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ZeTwitchMiner/" + Current);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public static Version Current { get; } = Version.TryParse(
        typeof(Updater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0],
        out var v) ? v : new Version(0, 0, 0);

    public bool ShowBanner => Available is not null && !Dismissed;
    public string VersionText => Available is null ? "" : Loc.F("Update.Available", Available.Version.ToString(3));

    partial void OnAvailableChanged(UpdateInfo? value)
    {
        OnPropertyChanged(nameof(ShowBanner));
        OnPropertyChanged(nameof(VersionText));
    }

    partial void OnDismissedChanged(bool value) => OnPropertyChanged(nameof(ShowBanner));

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
            while (!ct.IsCancellationRequested)
            {
                await CheckAsync(manual: false, ct);
                await Task.Delay(CheckInterval, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task CheckAsync(bool manual, CancellationToken ct = default)
    {
        if (IsBusy) return;
        try
        {
            if (manual) Message = Loc.T("Update.Checking");
            var json = JsonNode.Parse(await _http.GetStringAsync(LatestUrl, ct))!;
            var tag = json["tag_name"]?.GetValue<string>()?.TrimStart('v', 'V') ?? "";
            if (!Version.TryParse(tag, out var latest)) return;

            if (latest <= Current)
            {
                Available = null;
                if (manual) Message = Loc.T("Update.UpToDate");
                return;
            }

            UpdateAsset? Find(string suffix) => (json["assets"] as JsonArray ?? [])
                .Where(a => a?["name"]?.GetValue<string>().EndsWith(suffix, StringComparison.OrdinalIgnoreCase) == true)
                .Select(a => new UpdateAsset(
                    a!["name"]!.GetValue<string>(),
                    a["browser_download_url"]!.GetValue<string>(),
                    a["digest"]?.GetValue<string>()?.Replace("sha256:", "", StringComparison.OrdinalIgnoreCase) ?? "",
                    a["size"]?.GetValue<long>() ?? 0))
                .FirstOrDefault();

            Available = new UpdateInfo(latest, json["body"]?.GetValue<string>() ?? "", json["html_url"]?.GetValue<string>() ?? "",
                Find("-setup.exe"), Find("-portable.zip"));
            if (manual)
            {
                Dismissed = false;
                Message = null;
            }
            Log.Info($"Update available: {latest}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Debug("Update check failed: " + ex.Message);
            if (manual) Message = Loc.T("Update.CheckFailed");
        }
    }

    public async Task InstallAsync()
    {
        if (Available is not { } update || IsBusy) return;
        var asset = AppPaths.IsPortable ? update.Portable : update.Installer;
        if (asset is null || asset.Sha256.Length == 0)
        {
            Message = Loc.T("Update.NoAsset");
            return;
        }

        IsBusy = true;
        Message = Loc.T("Update.Downloading");
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "ZeTwitchMiner-update");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, asset.Name);
            await DownloadAsync(asset, file);

            // Ставим только то, что совпало с суммой, которую отдал GitHub
            await using (var stream = File.OpenRead(file))
            {
                var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
                if (!hash.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("checksum mismatch");
            }

            Message = Loc.T("Update.Installing");
            StartAfterExit(file, dir, AppPaths.IsPortable);

            Log.Info($"Installing update {update.Version}");
            await _exit();
        }
        catch (Exception ex)
        {
            Log.Error("Update failed: " + ex.Message);
            Message = Loc.T("Update.Failed");
            IsBusy = false;
        }
    }

    private async Task DownloadAsync(UpdateAsset asset, string file)
    {
        using var response = await _http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? asset.Size;
        await using var source = await response.Content.ReadAsStreamAsync();
        await using var target = File.Create(file);
        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await source.ReadAsync(buffer)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, n));
            read += n;
            if (total > 0) Progress = (double)read / total;
        }
    }

    // Пока программа запущена, exe не перезаписать, а установщик увидит её и прервётся.
    // Поэтому скрипт ждёт выхода, ставит обновление и запускает программу снова.
    private static void StartAfterExit(string file, string dir, bool portable)
    {
        var appDir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var exe = Path.Combine(appDir, Path.GetFileName(AppPaths.ExePath));
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("chcp 65001 >nul");
        sb.AppendLine($"set PID={Environment.ProcessId}");
        sb.AppendLine(":wait");
        sb.AppendLine("tasklist /FI \"PID eq %PID%\" | find \"%PID%\" >nul && (ping -n 2 127.0.0.1 >nul & goto wait)");

        if (portable)
        {
            var unpacked = Path.Combine(dir, "portable");
            if (Directory.Exists(unpacked)) Directory.Delete(unpacked, true);
            ZipFile.ExtractToDirectory(file, unpacked);
            sb.AppendLine($"robocopy \"{unpacked}\" \"{appDir}\" /E /XD data /XF portable /NFL /NDL /NJH /NJS /NP >nul");
            sb.AppendLine($"start \"\" \"{exe}\"");
        }
        else
        {
            // Установщик в тихом режиме сам запустит программу после установки
            sb.AppendLine($"\"{file}\" /SILENT /SUPPRESSMSGBOXES /NORESTART");
        }
        sb.AppendLine("del \"%~f0\"");

        var script = Path.Combine(dir, "update.cmd");
        File.WriteAllText(script, sb.ToString(), new UTF8Encoding(false));
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
        {
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            UseShellExecute = false,
        });
    }

    public void OpenReleasePage()
    {
        if (Available?.PageUrl is { Length: > 0 } url)
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
