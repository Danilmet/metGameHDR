using Microsoft.Win32;

namespace MetGameHUB.Ui;

public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "metGameHUB";
    const string LegacyValueName = "metGameHDR";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) != null;
    }

    public static void Set(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --tray");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    // one-time rewrite after the rename: the old value points at an exe that no longer exists
    public static void MigrateLegacy()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(LegacyValueName) is null) return;
        key.DeleteValue(LegacyValueName);
        key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --tray");
    }
}
