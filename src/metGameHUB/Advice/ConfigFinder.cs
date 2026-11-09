using System.IO;
using System.Text;
using MetGameHUB.Scan;

namespace MetGameHUB.Advice;

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
    public const int MaxFiles = 3, MaxTotalBytes = 32 * 1024, MaxRootFileBytes = 16 * 1024, MaxFileBytes = 1024 * 1024;

    private static readonly string[] Keywords = ["settings", "config", "video", "graphics", "user", "prefs"];
    private static readonly string[] SecretKeys = ["password", "passwd", "pwd", "token", "secret", "session", "auth", "ticket", "credential"];
    private static readonly HashSet<string> NoiseFolders = new(
        ["Microsoft", "Packages", "Temp", "Programs", "Google", "Mozilla", "NVIDIA", "AMD", "Steam", "Discord", "CrashDumps", "D3DSCache", "Comms"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly string[] EmuPrefixes = ["steam_emu", "cream_api"];

    public static IReadOnlyList<ConfigCandidate> Find(Game game, FinderRoots roots)
    {
        var result = new List<ConfigCandidate>();
        var total = 0;
        foreach (var path in Paths(game, roots).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileName(path);
            if (EmuPrefixes.Any(p => fileName.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;

            string text;
            try
            {
                if (new FileInfo(path).Length > MaxFileBytes) continue;
                text = MaskSecrets(ReadHead(path));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

            var bytes = Encoding.UTF8.GetBytes(text);
            var left = Math.Min(MaxTotalBytes - total, MaxRootFileBytes);
            var truncated = bytes.Length > left;
            if (truncated) text = Encoding.UTF8.GetString(bytes, 0, left);
            total += Math.Min(bytes.Length, left);
            result.Add(new ConfigCandidate(path, text, truncated));
            if (result.Count >= MaxFiles || total >= MaxTotalBytes) break;
        }
        return result;
    }

    private static string ReadHead(string path)
    {
        using var reader = new StreamReader(path);
        var buf = new char[MaxRootFileBytes + 1];
        return new string(buf, 0, reader.ReadBlock(buf, 0, buf.Length));
    }

    internal static string MaskSecrets(string text) => string.Join('\n', text.Split('\n').Select(MaskLine));

    internal static bool IsSecretKey(string key) => SecretKeys.Any(s => key.Contains(s, StringComparison.OrdinalIgnoreCase));

    private static string MaskLine(string line)
    {
        var body = line.TrimEnd('\r');
        var start = body.Length - body.TrimStart().Length;
        if (start == body.Length || body[start] == '[') return line;
        var sep = body.IndexOfAny(['=', ' ', '\t'], start);
        if (sep < 0) return line;
        var key = body[start..sep];
        if (!IsSecretKey(key)) return line;

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

    internal static bool NameMatchesLoose(string folder, Game game)
    {
        var n = Normalize(folder);
        if (n.Length < 4) return false;
        var names = new List<string> { game.Name };
        if (!string.IsNullOrEmpty(game.InstallDir))
            names.Add(Path.GetFileName(game.InstallDir.TrimEnd('\\', '/')));
        if (!string.IsNullOrEmpty(game.MainExe)) names.Add(Path.GetFileNameWithoutExtension(game.MainExe));
        return names.Select(Normalize).Any(x => x.Length >= 4 && (x.Contains(n) || n.Contains(x)));
    }

    public static string? UnrealProject(string installDir) => SafeDirs(installDir).Order(StringComparer.OrdinalIgnoreCase)
        .Where(p => Directory.Exists(Path.Combine(p, "Binaries")) && Directory.Exists(Path.Combine(p, "Content")))
        .Select(p => Path.GetFileName(p)).FirstOrDefault();

    private static IEnumerable<string> UnrealSettings(string projectDir)
    {
        foreach (var platform in SafeDirs(Path.Combine(projectDir, "Saved", "Config")))
        {
            var ini = Path.Combine(platform, "GameUserSettings.ini");
            if (File.Exists(ini)) yield return ini;
        }
    }

    private static IEnumerable<string> Paths(Game game, FinderRoots roots)
    {
        var installed = Directory.Exists(game.InstallDir);
        if (installed)
            foreach (var p in SafeDirs(game.InstallDir))
                if (Directory.Exists(Path.Combine(p, "Binaries")) && Directory.Exists(Path.Combine(p, "Content")))
                    foreach (var ini in UnrealSettings(Path.Combine(roots.LocalAppData, Path.GetFileName(p))))
                        yield return ini;

        foreach (var dir in MatchingDirs(roots.LocalAppData, game))
            foreach (var ini in UnrealSettings(dir))
                yield return ini;

        foreach (var top in SafeDirs(roots.LocalAppData))
        {
            if (NoiseFolders.Contains(Path.GetFileName(top))) continue;
            foreach (var dir in new[] { top }.Concat(SafeDirs(top)))
            {
                if (!NameMatchesLoose(Path.GetFileName(dir), game)) continue;
                foreach (var pattern in new[] { "*.ini", "*.cfg" })
                    foreach (var f in SafeFiles(dir, pattern)) yield return f;
                foreach (var ini in UnrealSettings(dir)) yield return ini;
            }
        }

        foreach (var root in new[] { Path.Combine(roots.Documents, "My Games"), roots.SavedGames })
            foreach (var dir in MatchingDirs(root, game))
                foreach (var pattern in new[] { "*.ini", "*.cfg", "*.txt" })
                    foreach (var f in SafeFiles(dir, pattern))
                    {
                        var name = Path.GetFileName(f).ToLowerInvariant();
                        if (Keywords.Any(name.Contains)) yield return f;
                    }

        if (installed)
        {
            foreach (var pattern in new[] { "*.ini", "*.cfg" })
                foreach (var f in SafeFiles(game.InstallDir, pattern))
                    if (WithinSize(f, MaxRootFileBytes)) yield return f;
        }
    }

    private static IEnumerable<string> MatchingDirs(string root, Game game) =>
        SafeDirs(root).Where(d => NameMatches(Path.GetFileName(d), game));

    private static IEnumerable<string> SafeDirs(string dir) => Safe(() => Directory.GetDirectories(dir));

    private static IEnumerable<string> SafeFiles(string dir, string pattern) =>
        Safe(() => Directory.GetFiles(dir, pattern))
            .OrderBy(f => !Path.GetFileName(f).Contains("prefs", StringComparison.OrdinalIgnoreCase))
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase);

    private static string[] Safe(Func<string[]> list)
    {
        try { return list(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }
}
