using MetGameHDR.Scan;

namespace MetGameHDR.Watch;

public sealed class ProcessWatcher(Library lib, Func<int[]> pids, Func<int, string?> path)
{
    HashSet<int> seen = [];
    readonly Dictionary<int, Game> games = [];
    readonly List<int> gone = [];

    public IReadOnlyCollection<int> GamePids => games.Keys;

    public bool Tick()
    {
        var current = pids();
        var next = new HashSet<int>(current);
        foreach (var pid in current)
        {
            if (seen.Contains(pid)) continue;
            var p = path(pid);
            if (p != null && lib.FindByPath(p) is { } g) games[pid] = g;
        }
        seen = next;

        gone.Clear();
        foreach (var pid in games.Keys) if (!seen.Contains(pid)) gone.Add(pid);
        foreach (var pid in gone) games.Remove(pid);
        return games.Count > 0;
    }
}
