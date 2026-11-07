using MetGameHDR.Steam;

namespace MetGameHDR.Scan;

public static class LibraryScanner
{
    public static List<Game> Scan(IEnumerable<IGameSource> sources, Action<string> log)
    {
        var games = new List<Game>();
        foreach (var source in sources)
        {
            try { games.AddRange(source.Scan()); }
            catch (Exception e) { log($"{source.Source}: {e.Message}"); }
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return games.Where(g => seen.Add(g.InstallDir)).ToList();
    }

    public static IEnumerable<IGameSource> DefaultSources(Settings s) =>
    [
        new SteamSource(SteamLocator.SteamPath()),
        new EpicSource(),
        new GogSource(),
        new UbisoftSource(),
        new EaSource(),
        new XboxSource(),
        new FolderSource(s.FolderRoots),
        new AppsSource(s.Apps),
    ];
}
