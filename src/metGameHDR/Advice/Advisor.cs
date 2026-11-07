using System.IO;
using System.Net.Http;
using MetGameHDR.Scan;

namespace MetGameHDR.Advice;

public sealed record EditPreview(ConfigEdit Edit, string? Current);

public enum ApplyOutcome { Applied, GameRunning, NoConfig, PathNotCandidate, FileMissing }

public sealed class Advisor(Func<string?> apiKey, Library lib, string libraryPath, Func<HardwareInfo> hardware,
    FinderRoots roots, Func<Game, bool> isRunning, HttpClient http)
{
    public async Task<Advice> AskAsync(Game game, GraphicsGoal goal, CancellationToken ct)
    {
        var key = apiKey();
        if (string.IsNullOrWhiteSpace(key)) throw new AdviceException("Не задан ключ API");
        var hw = hardware();
        var candidates = ConfigFinder.Find(game, roots);
        var result = await new ClaudeClient(http, key).AskAsync(new AdviceRequest(game, hw, goal, candidates), ct);
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
        if (!IsCandidate(game, roots, config.Path)) return ApplyOutcome.PathNotCandidate;
        var path = Path.GetFullPath(config.Path);
        if (!File.Exists(path)) return ApplyOutcome.FileMissing;
        if (isRunning(game)) return ApplyOutcome.GameRunning;

        var original = File.ReadAllBytes(path);
        if (!File.Exists(path + ".orig")) File.WriteAllBytes(path + ".orig", original);
        File.WriteAllBytes(path + ".bak", original);

        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, IniEditor.ApplyBytes(original, config.Edits));
        File.Move(tmp, path, overwrite: true);
        return ApplyOutcome.Applied;
    }
}
