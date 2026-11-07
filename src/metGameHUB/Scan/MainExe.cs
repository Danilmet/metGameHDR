using System.IO;
using System.Text.RegularExpressions;

namespace MetGameHUB.Scan;

public static class MainExe
{
    private static readonly string[] ServicePatterns = ["unins*", "crash*", "*redist*", "vcredist*", "dxsetup*", "setup*"];

    private static readonly Regex[] ServiceRegexes = ServicePatterns
        .Select(p => new Regex("^" + Regex.Escape(p).Replace(@"\*", ".*") + "$", RegexOptions.IgnoreCase))
        .ToArray();

    public static IReadOnlyList<string> Candidates(string gameDir)
    {
        if (!Directory.Exists(gameDir)) return [];
        return Directory.EnumerateFiles(gameDir, "*.exe", SearchOption.TopDirectoryOnly)
            .Where(f => !ServiceRegexes.Any(r => r.IsMatch(Path.GetFileName(f))))
            .ToList();
    }

    public static string? Pick(string gameDir, string gameName)
    {
        var candidates = Candidates(gameDir);
        if (candidates.Count == 0) return null;

        var name = Normalize(gameName);
        var matches = candidates
            .Select(c => (Path: c, Exe: Normalize(Path.GetFileNameWithoutExtension(c))))
            .Select(x => (x.Path, x.Exe, Dist: Levenshtein(x.Exe, name)))
            .Where(x => x.Dist <= name.Length / 2 || IsSubstringMatch(x.Exe, name))
            .ToList();
        if (matches.Count > 0) return matches.MinBy(x => x.Dist).Path;

        return candidates.MaxBy(SafeLength);
    }

    private static bool IsSubstringMatch(string a, string b)
    {
        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        return shorter.Length >= 3 && longer.Contains(shorter, StringComparison.Ordinal);
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    private static string Normalize(string s) =>
        new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
