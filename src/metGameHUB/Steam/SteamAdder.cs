using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using MetGameHUB.Scan;

namespace MetGameHUB.Steam;

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

        var db = string.IsNullOrWhiteSpace(s.SteamGridDbKey) ? null : new SteamGridDb(http, s.SteamGridDbKey);
        // Upserting into a throwaway copy only yields each appid, so artwork downloads while Steam keeps running.
        var draft = ShortcutsFile.Load(file);
        var toWrite = new List<(Game Game, string Exe, string Icon)>();
        foreach (var g in games)
        {
            var exe = lib.MainExeOf(g);
            if (exe is null) { log($"{g.Name}: нет exe"); continue; }
            var appId = ShortcutsFile.AppIdOf(ShortcutsFile.Upsert(draft, g.Name, exe, g.InstallDir, "", g.LaunchOptions ?? ""));
            string? icon = null;
            try
            {
                icon = Artwork.SaveIcon(exe, gridDir, appId);
                if (db != null)
                    await Artwork.DownloadAsync(db, http, gridDir, appId, lib.SearchNameOf(g));
            }
            catch (Exception e) { log($"{g.Name}: {e.Message}"); }
            toWrite.Add((g, exe, icon ?? ""));
        }
        if (toWrite.Count == 0) return Statuses(draft);

        var wasRunning = SteamProcess.IsRunning();
        VdfObject? root = null;
        try
        {
            if (wasRunning) await SteamProcess.ShutdownAsync(steamPath);
            root = ShortcutsFile.Load(file); // fresh: Steam rewrites the file on exit
            foreach (var (g, exe, icon) in toWrite)
                ShortcutsFile.Upsert(root, g.Name, exe, g.InstallDir, icon, g.LaunchOptions ?? "");
        }
        finally
        {
            try { if (root != null) ShortcutsFile.Save(file, root); }
            finally { if (wasRunning) SteamProcess.Start(steamPath); }
        }

        return Statuses(root);

        Dictionary<Game, SteamStatus> Statuses(VdfObject r)
        {
            var result = new Dictionary<Game, SteamStatus>();
            foreach (var g in games) result[g] = StatusOf(g, r, gridDir);
            return result;
        }
    }
}
