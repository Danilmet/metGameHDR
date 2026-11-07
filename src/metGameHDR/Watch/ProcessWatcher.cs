using MetGameHDR.Scan;

namespace MetGameHDR.Watch;

public sealed class ProcessWatcher(Library lib, Func<int[]> pids, Func<int, string?> path)
{
    HashSet<int> seen = [];
    readonly Dictionary<int, Game> games = [];
    readonly List<int> gone = [];

    public IReadOnlyCollection<int> GamePids => games.Keys;

    public void Reset()
    {
        seen.Clear();
        games.Clear();
    }

    public bool Tick()
    {
        gone.Clear();
        foreach (var (pid, game) in games)
            if (path(pid) is not { } p || lib.FindByPath(p) != game) gone.Add(pid);
        foreach (var pid in gone)
        {
            games.Remove(pid);
            seen.Remove(pid); // PID reused: let the loop below classify the new process
        }

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
