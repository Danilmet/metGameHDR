using System.IO;
using MetGameHDR.Steam;

namespace MetGameHDR.Scan;

public sealed class SteamSource(string? steamPath) : IGameSource
{
    public GameSource Source => GameSource.Steam;

    public IEnumerable<Game> Scan()
    {
        if (steamPath == null) yield break;
        var libs = Load(Path.Combine(steamPath, "steamapps", "libraryfolders.vdf"));
        if (libs == null || !libs.TryGetValue("libraryfolders", out var lf) || lf is not Dictionary<string, object> folders) yield break;

        foreach (var entry in folders.Values)
        {
            if (entry is not Dictionary<string, object> lib || !lib.TryGetValue("path", out var p) || p is not string libPath) continue;
            var apps = Path.Combine(libPath, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var file in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                var state = Load(file);
                if (state == null || !state.TryGetValue("AppState", out var a) || a is not Dictionary<string, object> app) continue;
                if (app.GetValueOrDefault("name") is not string name || app.GetValueOrDefault("installdir") is not string dir) continue;
                yield return new Game(name, Path.Combine(apps, "common", dir), GameSource.Steam, null);
            }
        }
    }

    static Dictionary<string, object>? Load(string file)
    {
        if (!File.Exists(file)) return null;
        try { return TextVdf.Parse(File.ReadAllText(file)); }
        catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException) { return null; }
    }
}
