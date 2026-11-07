using System.IO;
using System.Text;
using MetGameHDR.Scan;

namespace MetGameHDR.Advice;

public sealed record ConfigCandidate(string Path, string Text, bool Truncated);

public sealed record FinderRoots(string LocalAppData, string Documents, string SavedGames)
{
    public static FinderRoots Default() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games"));
}

public static class ConfigFinder
{
    public const int MaxFiles = 3, MaxTotalBytes = 100 * 1024, MaxRootFileBytes = 64 * 1024, MaxFileBytes = 1024 * 1024;

    private static readonly string[] Keywords = ["settings", "config", "video", "graphics", "user"];
    private static readonly string[] SecretKeys = ["pass", "pwd", "token", "secret", "session", "auth", "ticket", "credential"];
    private static readonly string[] EmuPrefixes = ["steam_emu", "cream_api"];

    public static IReadOnlyList<ConfigCandidate> Find(Game game, FinderRoots roots)
    {
        var result = new List<ConfigCandidate>();
        var total = 0;
        foreach (var path in Paths(game, roots))
        {
            var fileName = Path.GetFileName(path);
            if (EmuPrefixes.Any(p => fileName.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;

            string text;
            try
            {
                if (new FileInfo(path).Length > MaxFileBytes) continue;
                text = MaskSecrets(File.ReadAllText(path));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

            var bytes = Encoding.UTF8.GetBytes(text);
            var left = MaxTotalBytes - total;
            var truncated = bytes.Length > left;
            if (truncated) text = Encoding.UTF8.GetString(bytes, 0, left);
            total += Math.Min(bytes.Length, left);
            result.Add(new ConfigCandidate(path, text, truncated));
            if (result.Count >= MaxFiles || total >= MaxTotalBytes) break;
        }
        return result;
    }

    internal static string MaskSecrets(string text) => string.Join('\n', text.Split('\n').Select(MaskLine));

    private static string MaskLine(string line)
    {
        var body = line.TrimEnd('\r');
        var start = body.Length - body.TrimStart().Length;
        if (start == body.Length || body[start] == '[') return line;
        var sep = body.IndexOfAny(['=', ' ', '\t'], start);
        if (sep < 0) return line;
        var key = body[start..sep];
        if (!SecretKeys.Any(s => key.Contains(s, StringComparison.OrdinalIgnoreCase))) return line;

        var i = sep;
        while (i < body.Length && char.IsWhiteSpace(body[i])) i++;
        if (i < body.Length && body[i] == '=') i++;
        while (i < body.Length && char.IsWhiteSpace(body[i])) i++;
        return body[..i] + "***" + line[body.Length..];
    }

    private static bool WithinSize(string path, int max)
    {
        try { return new FileInfo(path).Length <= max; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    internal static string Normalize(string name) =>
        new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    internal static bool NameMatches(string folder, Game game)
    {
        var n = Normalize(folder);
        if (n.Length == 0) return false;
        var names = new List<string> { game.Name };
        if (!string.IsNullOrEmpty(game.InstallDir))
            names.Add(Path.GetFileName(game.InstallDir.TrimEnd('\\', '/')));
        if (!string.IsNullOrEmpty(game.MainExe)) names.Add(Path.GetFileNameWithoutExtension(game.MainExe));
        return names.Any(x => Normalize(x) == n);
    }

    private static IEnumerable<string> Paths(Game game, FinderRoots roots)
    {
        foreach (var dir in MatchingDirs(roots.LocalAppData, game))
        {
            var configs = Path.Combine(dir, "Saved", "Config");
            foreach (var platform in SafeDirs(configs))
            {
                var ini = Path.Combine(platform, "GameUserSettings.ini");
                if (File.Exists(ini)) yield return ini;
            }
        }

        if (Directory.Exists(game.InstallDir))
        {
            foreach (var pattern in new[] { "*.ini", "*.cfg" })
                foreach (var f in SafeFiles(game.InstallDir, pattern))
                    if (WithinSize(f, MaxRootFileBytes)) yield return f;
        }

        foreach (var root in new[] { Path.Combine(roots.Documents, "My Games"), roots.SavedGames })
            foreach (var dir in MatchingDirs(root, game))
                foreach (var pattern in new[] { "*.ini", "*.cfg", "*.txt" })
                    foreach (var f in SafeFiles(dir, pattern))
                    {
                        var name = Path.GetFileName(f).ToLowerInvariant();
                        if (Keywords.Any(name.Contains)) yield return f;
                    }
    }

    private static IEnumerable<string> MatchingDirs(string root, Game game) =>
        SafeDirs(root).Where(d => NameMatches(Path.GetFileName(d), game));

    private static IEnumerable<string> SafeDirs(string dir) => Safe(() => Directory.GetDirectories(dir));

    private static IEnumerable<string> SafeFiles(string dir, string pattern) =>
        Safe(() => Directory.GetFiles(dir, pattern)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

    private static string[] Safe(Func<string[]> list)
    {
        try { return list(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }
}
