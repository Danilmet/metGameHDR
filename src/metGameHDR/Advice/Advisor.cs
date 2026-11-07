using System.IO;
using System.Net.Http;
using MetGameHDR.Scan;

namespace MetGameHDR.Advice;

public sealed record EditPreview(ConfigEdit Edit, string? Current);

public enum ApplyOutcome { Applied, GameRunning, NoConfig, PathNotCandidate, FileMissing, NotIni, BadEdit }

public sealed class Advisor(Func<string?> apiKey, Library lib, string libraryPath, Func<HardwareInfo> hardware,
    FinderRoots roots, Func<Game, bool> isRunning, HttpClient http)
{
    public async Task<Advice> AskAsync(Game game, GraphicsGoal goal, CancellationToken ct)
    {
        var key = apiKey();
        if (string.IsNullOrWhiteSpace(key)) throw new AdviceException("Не задан ключ API");
        var hw = hardware();
        var effective = game with { Name = lib.SearchNameOf(game), MainExe = lib.MainExeOf(game) };
        var candidates = ConfigFinder.Find(effective, roots);
        var result = await new ClaudeClient(http, key).AskAsync(new AdviceRequest(effective, hw, goal, candidates), ct);
        var advice = new Advice(result, DateTime.UtcNow);
        var o = lib.For(game);
        o.Goal = goal;
        o.Advice = advice;
        lib.Save(libraryPath);
        return advice;
    }

    public static bool IsCandidate(Game game, FinderRoots roots, string path)
    {
        var target = Norm(path);
        return ConfigFinder.Find(game, roots).Any(c => string.Equals(Norm(c.Path), target, StringComparison.OrdinalIgnoreCase));
    }

    public static bool EditsValid(ConfigAdvice config) => config.Edits.All(e =>
        !string.IsNullOrWhiteSpace(e.Key) && e.Key.IndexOfAny(['=', '\r', '\n']) < 0
        && e.Value is not null && e.Value.IndexOfAny(['\r', '\n']) < 0);

    public static bool LooksLikeIni(byte[] file)
    {
        // Leading BOM bytes, NULs (UTF-16) and whitespace are skipped to reach the first real character.
        var first = file.SkipWhile(b => b is 0xEF or 0xBB or 0xBF or 0xFF or 0xFE or 0 or 9 or 10 or 13 or 32).FirstOrDefault();
        return first is not ((byte)'{' or (byte)'<');
    }

    static string Norm(string p) => Path.GetFullPath(p).TrimEnd('\\', '/');

    public IReadOnlyList<EditPreview> Preview(Game game, Advice advice)
    {
        var config = advice.Result.Config;
        if (config is null) return [];
        string? text = null;
        try { text = File.ReadAllText(config.Path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return config.Edits
            .Select(x => new EditPreview(x, text is null ? null : IniEditor.CurrentValue(text, x.Section, x.Key)))
            .ToList();
    }

    public ApplyOutcome Apply(Game game, Advice advice)
    {
        var config = advice.Result.Config;
        if (config is null) return ApplyOutcome.NoConfig;
        if (!EditsValid(config)) return ApplyOutcome.BadEdit;
        if (!IsCandidate(game, roots, config.Path)) return ApplyOutcome.PathNotCandidate;
        var path = Path.GetFullPath(config.Path);
        if (!File.Exists(path)) return ApplyOutcome.FileMissing;
        if (isRunning(game)) return ApplyOutcome.GameRunning;

        var original = File.ReadAllBytes(path);
        if (!LooksLikeIni(original)) return ApplyOutcome.NotIni;
        if (!File.Exists(path + ".orig")) File.WriteAllBytes(path + ".orig", original);
        File.WriteAllBytes(path + ".bak", original);

        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, IniEditor.ApplyBytes(original, config.Edits));
        try { File.Move(tmp, path, overwrite: true); }
        catch { File.Delete(tmp); throw; }
        return ApplyOutcome.Applied;
    }
}
