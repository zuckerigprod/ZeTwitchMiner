using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZeTwitchMiner.Core;

public enum ThemeMode { System, Dark, Light }

public enum PriorityMode { PriorityOnly, EndingSoonest, LowAvailFirst }

public sealed class PointsEntry
{
    public string Login { get; set; } = "";
    public string Name { get; set; } = "";
    public string Avatar { get; set; } = "";
}

public sealed class Settings
{
    public string Language { get; set; } = "";
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public List<string> PriorityGames { get; set; } = [];
    public List<string> ExcludedGames { get; set; } = [];
    public PriorityMode PriorityMode { get; set; } = PriorityMode.PriorityOnly;
    public bool AvailableDropsCheck { get; set; } = false;
    public bool EnableBadgesEmotes { get; set; } = false;
    public bool StartWithWindows { get; set; } = false;
    public bool StartInTray { get; set; } = false;
    public bool MinimizeToTray { get; set; } = true;
    public bool Notifications { get; set; } = true;
    public string Proxy { get; set; } = "";
    public int ConnectionQuality { get; set; } = 1;
    public bool PointsEnabled { get; set; }
    public bool PointsUseModule { get; set; }
    public List<PointsEntry> PointsChannels { get; set; } = [];
    public double WindowWidth { get; set; } = 1080;
    public double WindowHeight { get; set; } = 700;

    public static Settings Load()
    {
        try
        {
            if (File.Exists(AppPaths.Settings))
            {
                var json = File.ReadAllText(AppPaths.Settings);
                return JsonSerializer.Deserialize(json, AppJson.Default.Settings) ?? new Settings();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("settings.json is broken, using defaults: " + ex.Message);
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            var tmp = AppPaths.Settings + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, AppJson.Default.Settings));
            File.Move(tmp, AppPaths.Settings, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save settings: " + ex.Message);
        }
    }
}
