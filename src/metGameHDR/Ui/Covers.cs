using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MetGameHDR.Scan;
using MetGameHDR.Steam;

namespace MetGameHDR.Ui;

public static class Covers
{
    public static string? LocalPath(Game game, string? steamPath, VdfObject? shortcuts, string? gridDir, string cacheDir)
    {
        if (steamPath is not null && game.SteamAppId is { } id)
        {
            var lib = Path.Combine(steamPath, "appcache", "librarycache");
            foreach (var p in new[] { Path.Combine(lib, id, "library_600x900.jpg"), Path.Combine(lib, id + "_library_600x900.jpg") })
                if (File.Exists(p)) return p;
        }

        if (shortcuts is not null && gridDir is not null && Directory.Exists(gridDir)
            && ShortcutsFile.Find(shortcuts, game.InstallDir) is { } entry)
        {
            var p = Directory.EnumerateFiles(gridDir, ShortcutsFile.AppIdOf(entry) + "p.*").FirstOrDefault();
            if (p is not null) return p;
        }

        return Directory.Exists(cacheDir)
            ? Directory.EnumerateFiles(cacheDir, CacheFileName(game.InstallDir) + ".*").FirstOrDefault()
            : null;
    }

    public static string CacheFileName(string installDir)
    {
        var name = Path.GetFileName(installDir.TrimEnd('\\', '/'));
        var safe = string.Concat(name.Select(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c));
        if (safe.Length > 60) safe = safe[..60];
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(installDir.ToUpperInvariant()));
        return safe.ToLowerInvariant() + "-" + Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }
}
