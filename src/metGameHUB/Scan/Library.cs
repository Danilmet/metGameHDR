using System.IO;
using System.Text.Json;

namespace MetGameHUB.Scan;

public sealed class Library
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public List<Game> Games { get; set; } = [];
    public Dictionary<string, GameOverride> Overrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public GameOverride For(Game g)
    {
        if (!Overrides.TryGetValue(g.InstallDir, out var o)) Overrides[g.InstallDir] = o = new GameOverride();
        return o;
    }

    public Game? FindByPath(string exePath) =>
        Games.Where(g => g.Source != GameSource.App
                         && (!Overrides.TryGetValue(g.InstallDir, out var o) || o.HdrEnabled && !o.Hidden)
                         && exePath.StartsWith(g.InstallDir + '\\',StringComparison.OrdinalIgnoreCase))
             .MaxBy(g => g.InstallDir.Length);

    public string? MainExeOf(Game g) => Overrides.TryGetValue(g.InstallDir, out var o) && o.MainExe != null ? o.MainExe : g.MainExe;

    public string SearchNameOf(Game g) => Overrides.TryGetValue(g.InstallDir, out var o) && o.SearchName != null ? o.SearchName : g.Name;

    public static Library Load(string path)
    {
        if (!File.Exists(path)) return new Library();
        Library lib;
        try { lib = JsonSerializer.Deserialize<Library>(File.ReadAllText(path), Options) ?? new Library(); }
        catch (Exception e) when (e is JsonException or IOException)
        {
            Settings.SetAside(path, e);
            return new Library();
        }
        lib.Overrides = new(lib.Overrides, StringComparer.OrdinalIgnoreCase);
        return lib;
    }

    public void Save(string path) => Settings.WriteAtomic(path, JsonSerializer.Serialize(this, Options));
}
