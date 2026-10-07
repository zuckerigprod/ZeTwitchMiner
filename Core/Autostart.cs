using Microsoft.Win32;

namespace ZeTwitchMiner.Core;

public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ZeTwitchMiner";

    public static void Apply(bool enabled, bool tray)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;
            if (enabled)
                key.SetValue(ValueName, $"\"{AppPaths.ExePath}\"" + (tray ? " --tray" : ""));
            else if (key.GetValue(ValueName) is not null)
                key.DeleteValue(ValueName);
        }
        catch (Exception ex)
        {
            Log.Warn("Autostart update failed: " + ex.Message);
        }
    }
}
