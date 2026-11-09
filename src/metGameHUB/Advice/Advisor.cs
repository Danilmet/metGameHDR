using System.IO;
using System.Net.Http;
using MetGameHUB.Scan;

namespace MetGameHUB.Advice;

public enum ApplyOutcome { Applied, GameRunning, NoConfig, FileMissing, NotIni, BadEdit }

public sealed class Advisor(Func<string?> apiKey, Func<string?> baseUrl, Func<string?> model, Library lib, string libraryPath, Func<HardwareInfo> hardware,
    FinderRoots roots, Func<Game, bool> isRunning, HttpClient http)
{
    const int MaxRootEntries = 200, MaxConfigChars = 32 * 1024;

    ClaudeClient Client() =>
        apiKey() is { } key && !string.IsNullOrWhiteSpace(key)
            ? new ClaudeClient(http, key, baseUrl(), model())
            : throw new AdviceException("Не задан ключ API");

    public async Task<ConfigStatus> LocateAsync(Game game, CancellationToken ct)
    {
        var client = Client();
        var effective = EffectiveGame(lib, game);
        var location = await client.LocateAsync(new LocateRequest(effective, RootEntries(effective.InstallDir),
            ConfigFinder.Find(effective, roots).Select(c => c.Path).ToList(), ConfigFinder.UnrealProject(effective.InstallDir)), ct);

        var note = location.Note;
        string? path = null;
        if (location.Path != null && location.Format is "ini" or "json")
        {
            path = TryResolve(location.Path, effective.InstallDir);
            if (path == null || !IsAllowedPath(path, effective.InstallDir, roots))
            {
                path = null;
                note = "Модель указала путь вне разрешённых папок";
            }
        }
        var o = lib.For(game);
        if (!string.Equals(o.ConfigPath, path, StringComparison.OrdinalIgnoreCase)) o.Snapshot = null;
        o.ConfigPath = path;
        o.ConfigNote = note ?? "";
        lib.Save(libraryPath);
        return path == null
            ? new ConfigStatus(null, false, "none", note)
            : new ConfigStatus(path, File.Exists(path), location.Format, note);
    }

    // The caller locates the config first; without a stored (and still allowed) path the request goes without a file.
    public async Task<Advice> AskAsync(Game game, GoalStep goal, CancellationToken ct)
    {
        var client = Client();
        var hw = hardware();
        var effective = EffectiveGame(lib, game);
        var path = LocatedPath(lib, roots, game);
        var current = IsSnapshotFresh(lib, roots, game) ? lib.For(game).Snapshot!.Settings : null;
        var request = ReadConfig(path) is { } c
            ? new AdviceRequest(effective, hw, goal, c.Path, c.Format, c.Text, c.Truncated, current)
            : new AdviceRequest(effective, hw, goal, null, null, null, false);
        var advice = new Advice(await client.AskAsync(request, ct), DateTime.UtcNow);
        var o = lib.For(game);
        o.Advice = advice;
        if (path != null && TryFileText(path) is { } text)
        {
            var rows = advice.Result.Settings.Where(s => s.Key != null).Select(s => new CurrentSetting(s.Name, s.Current, s.Section, s.Key)).ToList();
            o.Snapshot = new ConfigSnapshot(UnrealScalability.Merge(rows, text), DateTime.UtcNow, File.GetLastWriteTimeUtc(path));
        }
        lib.Save(libraryPath);
        return advice;
    }

    // Reads the located file and stores its current values; null (and no request) without an existing file.
    public async Task<ConfigSnapshot?> ReadAsync(Game game, CancellationToken ct)
    {
        var client = Client();
        var path = LocatedPath(lib, roots, game);
        if (ReadConfig(path) is not { } c || TryFileText(path!) is not { } text) return null;
        var result = await client.ReadAsync(new ReadRequest(EffectiveGame(lib, game), c.Path, c.Format, c.Text, c.Truncated), ct);
        var snapshot = new ConfigSnapshot(UnrealScalability.Merge(result.Settings, text), DateTime.UtcNow, File.GetLastWriteTimeUtc(path!));
        lib.For(game).Snapshot = snapshot;
        lib.Save(libraryPath);
        return snapshot;
    }

    public static bool IsSnapshotFresh(Library lib, FinderRoots roots, Game game) =>
        lib.Overrides.TryGetValue(game.InstallDir, out var o) && o.Snapshot is { } s
        && LocatedPath(lib, roots, game) is { } p && File.Exists(p) && File.GetLastWriteTimeUtc(p) == s.FileStamp;

    static string? TryFileText(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public static Game EffectiveGame(Library lib, Game game) =>
        game with { Name = lib.SearchNameOf(game), MainExe = lib.MainExeOf(game) };

    public static IReadOnlyList<ConfigEdit> Edits(AdviceResult result) =>
        result.Settings.Where(s => s.Key != null).Select(s => new ConfigEdit(s.Section, s.Key!, s.Value ?? "")).ToList();

    public static bool EditsValid(IReadOnlyList<ConfigEdit> edits) => edits.All(e =>
        !string.IsNullOrWhiteSpace(e.Key) && e.Key.IndexOfAny(['=', '\r', '\n']) < 0
        && e.Value is not null && e.Value.IndexOfAny(['\r', '\n']) < 0);

    // '<' (XML) is refused; '{' is accepted only as line JSON where every edited key already exists.
    internal static bool CanWrite(string text, IReadOnlyList<ConfigEdit> edits)
    {
        var first = text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n', '\0').FirstOrDefault();
        if (first == '<') return false;
        return first != '{' || edits.All(e => IniEditor.CurrentValue(text, null, e.Key) != null);
    }

    internal static string ResolvePath(string template, string installDir)
    {
        var expanded = Environment.ExpandEnvironmentVariables(template.Replace("<InstallDir>", installDir, StringComparison.OrdinalIgnoreCase));
        if (!Path.IsPathFullyQualified(expanded)) throw new ArgumentException("Путь не абсолютный", nameof(template));
        return Path.GetFullPath(expanded);
    }

    internal static bool IsAllowedPath(string path, string installDir, FinderRoots roots)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException) { return false; }
        return new[]
        {
            roots.LocalAppData, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            installDir, roots.Documents, roots.SavedGames,
        }.Any(r => !string.IsNullOrWhiteSpace(r)
            && full.StartsWith(r.TrimEnd('\\', '/') + '\\', StringComparison.OrdinalIgnoreCase));
    }

    public ApplyOutcome Apply(Game game, Advice advice)
    {
        var edits = Edits(advice.Result);
        if (edits.Count == 0) return ApplyOutcome.NoConfig;
        var path = LocatedPath(lib, roots, game);
        if (path == null || !File.Exists(path)) return ApplyOutcome.FileMissing;
        if (!EditsValid(edits)) return ApplyOutcome.BadEdit;
        if (isRunning(game)) return ApplyOutcome.GameRunning;

        var original = File.ReadAllBytes(path);
        if (!CanWrite(File.ReadAllText(path), edits)) return ApplyOutcome.NotIni;
        if (IniEditor.ApplyBytes(original, edits) is not { } updated)
        {
            Log.Write($"Apply advice: value does not fit the ANSI encoding of {path}");
            return ApplyOutcome.BadEdit;
        }
        if (!File.Exists(path + ".orig")) File.WriteAllBytes(path + ".orig", original);
        File.WriteAllBytes(path + ".bak", original);

        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, updated);
        try { File.Move(tmp, path, overwrite: true); }
        catch { File.Delete(tmp); throw; }
        // The file now holds the recommended values, so the stored advice shows them as current.
        var settings = advice.Result.Settings.Select(x => x.Key != null ? x with { Current = x.Recommended } : x).ToList();
        var o = lib.For(game);
        o.Advice = advice with { Result = advice.Result with { Settings = settings } };
        if (o.Snapshot is { } snap)
        {
            var rec = advice.Result.Settings.Where(x => x.Key != null).GroupBy(x => (x.Section, x.Key!)).ToDictionary(g => g.Key, g => g.Last().Recommended);
            o.Snapshot = snap with
            {
                Settings = snap.Settings.Select(x => x.Key != null && rec.TryGetValue((x.Section, x.Key), out var r) ? x with { Current = r } : x).ToList(),
                FileStamp = File.GetLastWriteTimeUtc(path),
            };
        }
        lib.Save(libraryPath);
        return ApplyOutcome.Applied;
    }

    // Puts <file>.orig (the state before the first apply) back; the replaced content goes to .bak as on apply.
    public ApplyOutcome Restore(Game game)
    {
        var path = LocatedPath(lib, roots, game);
        if (path == null || !File.Exists(path + ".orig")) return ApplyOutcome.FileMissing;
        if (isRunning(game)) return ApplyOutcome.GameRunning;

        if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
        var tmp = path + ".tmp";
        File.Copy(path + ".orig", tmp, overwrite: true);
        try { File.Move(tmp, path, overwrite: true); }
        catch { File.Delete(tmp); throw; }

        // Stored "current" values described the applied file; re-read them (raw) from the restored one.
        if (lib.Overrides.TryGetValue(game.InstallDir, out var o) && o.Advice is { } advice)
        {
            var text = File.ReadAllText(path);
            var settings = advice.Result.Settings.Select(x => x.Key != null ? x with { Current = IniEditor.CurrentValue(text, x.Section, x.Key) } : x).ToList();
            o.Advice = advice with { Result = advice.Result with { Settings = settings } };
        }
        if (lib.Overrides.TryGetValue(game.InstallDir, out var po) && po.Snapshot is { } snap)
        {
            var text = File.ReadAllText(path);
            po.Snapshot = snap with
            {
                Settings = snap.Settings.Select(x => x.Key != null && IniEditor.CurrentValue(text, x.Section, x.Key) is { } raw
                    ? x with { Current = UnrealScalability.Describe(x.Section, x.Key, raw)?.Current ?? raw } : x).ToList(),
                FileStamp = File.GetLastWriteTimeUtc(path),
            };
        }
        lib.Save(libraryPath);
        return ApplyOutcome.Applied;
    }

    static string? TryResolve(string template, string installDir)
    {
        try { return ResolvePath(template, installDir); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException) { return null; }
    }

    // The stored path is re-checked: library.json is user-editable and the file is later read and rewritten.
    public static string? LocatedPath(Library lib, FinderRoots roots, Game game) =>
        lib.Overrides.TryGetValue(game.InstallDir, out var o) && o.ConfigPath is { } p
        && TryResolve(p, game.InstallDir) is { } full && IsAllowedPath(full, game.InstallDir, roots) ? full : null;

    static (string Path, string Format, string Text, bool Truncated)? ReadConfig(string? path)
    {
        if (path == null || !File.Exists(path)) return null;
        try
        {
            using var reader = new StreamReader(path);
            var buf = new char[MaxConfigChars + 1];
            var n = reader.ReadBlock(buf, 0, buf.Length);
            var text = ConfigFinder.MaskSecrets(new string(buf, 0, Math.Min(n, MaxConfigChars)));
            var format = text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n').StartsWith('{') ? "json" : "ini";
            return (path, format, text, n > MaxConfigChars);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    static List<string> RootEntries(string installDir)
    {
        try
        {
            return new DirectoryInfo(installDir).EnumerateFileSystemInfos()
                .Select(i => i is DirectoryInfo ? i.Name + '\\' : i.Name)
                .Order(StringComparer.OrdinalIgnoreCase).Take(MaxRootEntries).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }
}
