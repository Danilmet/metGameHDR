using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using MetGameHDR.Scan;

namespace MetGameHDR.Steam;

public enum SteamStatus { NotAdded, Added, NoArtwork }

public static class SteamAdder
{
    public static SteamStatus StatusOf(Game g, VdfObject root, string gridDir)
    {
        var entry = ShortcutsFile.Find(root, g.InstallDir);
        if (entry is null) return SteamStatus.NotAdded;
        return Artwork.HasPortrait(gridDir, ShortcutsFile.AppIdOf(entry)) ? SteamStatus.Added : SteamStatus.NoArtwork;
    }

    public static async Task<Dictionary<Game, SteamStatus>> AddAsync(
        IReadOnlyList<Game> games, Library lib, Settings s, HttpClient http, Action<string> log)
    {
        var steamPath = SteamLocator.SteamPath() ?? throw new InvalidOperationException("Steam не найден");
        var userId = !string.IsNullOrWhiteSpace(s.SteamUserId) ? s.SteamUserId : SteamLocator.MostRecentUserId(steamPath)
            ?? throw new InvalidOperationException("Steam не найден");
        var configDir = SteamLocator.UserConfigDir(steamPath, userId);
        var file = Path.Combine(configDir, "shortcuts.vdf");
        var gridDir = Path.Combine(configDir, "grid");

        var wasRunning = SteamProcess.IsRunning();
        if (wasRunning) await SteamProcess.ShutdownAsync(steamPath);

        var root = ShortcutsFile.Load(file);
        try
        {
            foreach (var g in games)
            {
                var exe = lib.MainExeOf(g);
                if (exe is null) { log($"{g.Name}: нет exe"); continue; }
                var entry = ShortcutsFile.Upsert(root, g.Name, exe, g.InstallDir, "");
                var appId = ShortcutsFile.AppIdOf(entry);
                try
                {
                    var icon = Artwork.SaveIcon(exe, gridDir, appId);
                    if (icon != null) entry["icon"] = icon;
                    if (!string.IsNullOrWhiteSpace(s.SteamGridDbKey))
                        await Artwork.DownloadAsync(new SteamGridDb(http, s.SteamGridDbKey), http, gridDir, appId, lib.SearchNameOf(g));
                }
                catch (Exception e) { log($"{g.Name}: {e.Message}"); }
            }
        }
        finally
        {
            ShortcutsFile.Save(file, root);
            if (wasRunning) SteamProcess.Start(steamPath);
        }

        var result = new Dictionary<Game, SteamStatus>();
        foreach (var g in games) result[g] = StatusOf(g, root, gridDir);
        return result;
    }
}
