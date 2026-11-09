using System.IO;
using System.Net.Http;
using MetGameHUB.Scan;

namespace MetGameHUB.Advice;

public enum ApplyOutcome { Applied, GameRunning, NoConfig, FileMissing, NotIni, BadEdit }

public sealed class Advisor(Func<string?> apiKey, Func<string?> baseUrl, Func<ClaudeChoice> claude, Library lib, string libraryPath, Func<HardwareInfo> hardware,
    FinderRoots roots, Func<Game, bool> isRunning, HttpClient http)
{
    const int MaxRootEntries = 200, MaxConfigChars = 32 * 1024;

    ClaudeClient Client() =>
        apiKey() is { } key && !string.IsNullOrWhiteSpace(key)
            ? new ClaudeClient(http, key, baseUrl(), claude())
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
        if (!string.Equals(o.ConfigPath, path, StringComparison.OrdinalIgnoreCase)) { o.Snapshot = null; o.Chat = null; }
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
        var (request, path, fresh, stamp) = Prepare(game, goal);
        var advice = new Advice(await client.AskAsync(request, ct), DateTime.UtcNow);
        var o = lib.For(game);
        o.Advice = advice;
        o.Chat = null;
        var keyed = advice.Result.Settings.Where(s => s.Key != null).ToList();
        if (path != null && keyed.Count > 0 && ConfigStore.ReadText(path) is { } text)
        {
            var rows = fresh ? o.Snapshot!.Settings.ToList() : [];
            foreach (var s in keyed)
            {
                var i = rows.FindIndex(r => string.Equals(r.Section, s.Section, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(r.Key, s.Key, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) rows[i] = rows[i] with { Current = s.Current };
                else rows.Add(new CurrentSetting(s.Name, s.Current, s.Section, s.Key));
            }
            o.Snapshot = new ConfigSnapshot(UnrealScalability.Merge(rows, text), DateTime.UtcNow, stamp);
        }
        lib.Save(libraryPath);
        return advice;
    }

    (AdviceRequest Request, string? Path, bool Fresh, DateTime Stamp) Prepare(Game game, GoalStep goal)
    {
        var hw = hardware();
        var effective = EffectiveGame(lib, game);
        var path = LocatedPath(lib, roots, game);
        var fresh = IsSnapshotFresh(lib, roots, game);
        var stamp = path == null ? default : ConfigStore.Stamp(path); // before the request: a file rewritten meanwhile stays stale
        var current = fresh ? lib.For(game).Snapshot!.Settings.Where(r => r.Key == null || !ConfigFinder.IsSecretKey(r.Key)).ToList() : null;
        var request = ReadConfig(path) is { } c
            ? new AdviceRequest(effective, hw, goal, c.Path, c.Format, c.Text, c.Truncated, current)
            : new AdviceRequest(effective, hw, goal, null, null, null, false);
        return (request, path, fresh, stamp);
    }

    // The turns are appended together and only after success, so Chat always holds user/assistant pairs.
    public async Task<Advice> AdjustAsync(Game game, GoalStep goal, string text, IReadOnlyList<ChatAttachment> attachments, CancellationToken ct)
    {
        var client = Client();
        var o = lib.For(game);
        if (o.Advice is not { } previous) throw new AdviceException("Сначала подберите настройки");
        if (string.IsNullOrWhiteSpace(text) && attachments.Count == 0) throw new AdviceException("Пустое сообщение");
        var history = o.Chat ?? [];
        var sent = previous.Result with { Settings = previous.Result.Settings.Where(s => s.Key == null || !ConfigFinder.IsSecretKey(s.Key)).ToList() };
        var result = await client.AdjustAsync(new AdjustRequest(Prepare(game, goal).Request, sent, history, text, attachments), ct);

        var settings = result.Settings is { Count: > 0 } s ? s : previous.Result.Settings;
        var advice = new Advice(new AdviceResult(result.Summary, settings, result.Notes), DateTime.UtcNow);
        var reply = string.IsNullOrWhiteSpace(result.Reply) ? "(без комментария)" : result.Reply;
        o.Advice = advice;
        o.Chat = [.. history,
            new ChatTurn(ChatRole.User, text, attachments.Count > 0 ? attachments.Select(a => a.Name).ToList() : null, advice.At),
            new ChatTurn(ChatRole.Assistant, reply, null, advice.At)];
        lib.Save(libraryPath);
        return advice;
    }

    // Reads the located file and stores its current values; null (and no request) without an existing file.
    public async Task<ConfigSnapshot?> ReadAsync(Game game, CancellationToken ct)
    {
        var client = Client();
        var path = LocatedPath(lib, roots, game);
        var stamp = path == null ? default : ConfigStore.Stamp(path);
        if (ReadConfig(path) is not { } c || ConfigStore.ReadText(path!) is not { } text) return null;
        var result = await client.ReadAsync(new ReadRequest(EffectiveGame(lib, game), c.Path, c.Format, c.Text, c.Truncated), ct);
        var snapshot = new ConfigSnapshot(UnrealScalability.Merge(result.Settings, text), DateTime.UtcNow, stamp);
        lib.For(game).Snapshot = snapshot;
        lib.Save(libraryPath);
        return snapshot;
    }

    public static bool IsSnapshotFresh(Library lib, FinderRoots roots, Game game) =>
        lib.Overrides.TryGetValue(game.InstallDir, out var o) && o.Snapshot is { } s
        && LocatedPath(lib, roots, game) is { } p && ConfigStore.Exists(p) && ConfigStore.Stamp(p) == s.FileStamp;

    public static Game EffectiveGame(Library lib, Game game) =>
        game with { Name = lib.SearchNameOf(game), MainExe = lib.MainExeOf(game) };

    public static IReadOnlyList<ConfigEdit> Edits(AdviceResult result) =>
        result.Settings.Where(s => s.Key != null).Select(s => new ConfigEdit(s.Section, s.Key!, s.Value ?? "")).ToList();

    public static bool EditsValid(IReadOnlyList<ConfigEdit> edits) => edits.All(e =>
        !string.IsNullOrWhiteSpace(e.Key) && e.Key.IndexOfAny(['=', '\r', '\n']) < 0
        && e.Value is not null && e.Value.IndexOfAny(['\r', '\n']) < 0);

    internal static bool CanWrite(string text, IReadOnlyList<ConfigEdit> edits) => ConfigStore.CanWrite("", text, edits);

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
        if (path == null || !ConfigStore.Exists(path)) return ApplyOutcome.FileMissing;
        if (!EditsValid(edits)) return ApplyOutcome.BadEdit;
        if (isRunning(game)) return ApplyOutcome.GameRunning;

        var freshBefore = IsSnapshotFresh(lib, roots, game);
        if (ConfigStore.ReadText(path) is not { } text) return ApplyOutcome.FileMissing;
        if (!ConfigStore.CanWrite(path, text, edits)) return ApplyOutcome.NotIni;
        if (!ConfigStore.Apply(path, edits)) return ApplyOutcome.BadEdit;
        // The file now holds the recommended values, so the stored advice shows them as current.
        var settings = advice.Result.Settings.Select(x => x.Key != null ? x with { Current = x.Recommended } : x).ToList();
        var o = lib.For(game);
        o.Advice = advice with { Result = advice.Result with { Settings = settings } };
        if (o.Snapshot is { } snap)
        {
            // IniEditor matches section and key case-insensitively, so the rows must too.
            o.Snapshot = snap with
            {
                Settings = snap.Settings.Select(x => x.Key != null && advice.Result.Settings.LastOrDefault(a => a.Key != null
                    && string.Equals(a.Section, x.Section, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(a.Key, x.Key, StringComparison.OrdinalIgnoreCase)) is { } a ? x with { Current = a.Recommended } : x).ToList(),
                FileStamp = freshBefore ? ConfigStore.Stamp(path) : snap.FileStamp,
            };
        }
        lib.Save(libraryPath);
        return ApplyOutcome.Applied;
    }

    public void ClearChat(Game game)
    {
        if (!lib.Overrides.TryGetValue(game.InstallDir, out var o)) return;
        o.Chat = null;
        lib.Save(libraryPath);
    }

    // Puts <file>.orig (the state before the first apply) back; the replaced content goes to .bak as on apply.
    public ApplyOutcome Restore(Game game)
    {
        var path = LocatedPath(lib, roots, game);
        if (path == null || !ConfigStore.HasBackup(path)) return ApplyOutcome.FileMissing;
        if (isRunning(game)) return ApplyOutcome.GameRunning;

        var freshBefore = IsSnapshotFresh(lib, roots, game);
        ConfigStore.Restore(path);

        // Stored "current" values described the applied file; re-read them (raw) from the restored one.
        var text = ConfigStore.ReadText(path) ?? "";
        if (lib.Overrides.TryGetValue(game.InstallDir, out var o))
        {
            o.Chat = null;
            if (o.Advice is { } advice)
            {
                var settings = advice.Result.Settings.Select(x => x.Key != null ? x with { Current = ConfigStore.CurrentValue(path, text, x.Section, x.Key) } : x).ToList();
                o.Advice = advice with { Result = advice.Result with { Settings = settings } };
            }
            if (o.Snapshot is { } snap)
                o.Snapshot = snap with
                {
                    Settings = snap.Settings.Select(x => x.Key == null ? x
                        : x with { Current = ConfigStore.CurrentValue(path, text, x.Section, x.Key) is { } raw ? UnrealScalability.Describe(x.Section, x.Key, raw)?.Current ?? raw : null }).ToList(),
                    FileStamp = freshBefore ? ConfigStore.Stamp(path) : snap.FileStamp,
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
        if (path == null || !ConfigStore.Exists(path)) return null;
        if (ConfigStore.ReadText(path) is not { } raw) return null;
        var text = ConfigFinder.MaskSecrets(raw.Length > MaxConfigChars ? raw[..MaxConfigChars] : raw);
        var format = text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n').StartsWith('{') ? "json" : "ini";
        return (path, format, text, raw.Length > MaxConfigChars);
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
