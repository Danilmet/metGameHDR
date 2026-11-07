using System.IO;
using System.Text.RegularExpressions;

namespace MetGameHDR.Scan;

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
        var best = candidates
            .Select(c => (Path: c, Dist: Levenshtein(Normalize(Path.GetFileNameWithoutExtension(c)), name)))
            .MinBy(x => x.Dist);
        if (best.Dist <= name.Length / 2) return best.Path;

        return candidates.MaxBy(c => new FileInfo(c).Length);
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
