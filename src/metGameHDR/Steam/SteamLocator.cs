using System.IO;
using Microsoft.Win32;

namespace MetGameHDR.Steam;

public static class SteamLocator
{
    const ulong SteamId64Base = 76561197960265728;

    public static string? SteamPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        return (key?.GetValue("SteamPath") as string)?.Replace('/', '\\').TrimEnd('\\');
    }

    public static string? MostRecentUserId(string steamPath)
    {
        var file = Path.Combine(steamPath, "config", "loginusers.vdf");
        if (!File.Exists(file)) return null;
        Dictionary<string, object> root;
        try { root = TextVdf.Parse(File.ReadAllText(file)); }
        catch (FormatException) { return null; }
        if (!root.TryGetValue("users", out var u) || u is not Dictionary<string, object> users) return null;

        string? best = null;
        var bestTs = long.MinValue;
        foreach (var (id, v) in users)
        {
            if (!ulong.TryParse(id, out _) || v is not Dictionary<string, object> user) continue;
            var ts = user.TryGetValue("timestamp", out var t) && t is string s && long.TryParse(s, out var n) ? n : 0;
            if (ts > bestTs) { bestTs = ts; best = id; }
        }
        return best == null ? null : (ulong.Parse(best) - SteamId64Base).ToString();
    }

    public static string UserConfigDir(string steamPath, string userId) =>
        Path.Combine(steamPath, "userdata", userId, "config");
}
