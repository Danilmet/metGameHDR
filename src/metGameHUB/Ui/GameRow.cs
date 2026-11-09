using System.ComponentModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using MetGameHUB.Advice;
using MetGameHUB.Hltb;
using MetGameHUB.Scan;
using MetGameHUB.Steam;

namespace MetGameHUB.Ui;

public sealed class GameRow : INotifyPropertyChanged
{
    readonly Library lib;
    readonly Action onHdrChanged;
    SteamStatus? steamStatus;
    ImageSource? cover;
    ImageSource? icon;
    readonly FinderRoots roots;
    readonly Func<GoalStep> defaultTarget;
    readonly Func<int> hz;
    readonly Func<bool> hasKey;
    Advice.Advice? latest;
    bool canApply;
    bool isAsking;
    string askStage = "";
    string configText = "", configDetail = "";
    bool hasConfigPath, configMissing, canRestore, wasApplied;
    IReadOnlyList<ChatTurnVm> chat = [];
    string adviceStatus = "";
    string applyStatus = "";
    IReadOnlyList<AdviceRowVm> adviceRows = [];
    IReadOnlyList<CurrentSetting>? provisional;

    public GameRow(Game game, Library lib, SteamStatus? steamStatus, Action onHdrChanged,
        FinderRoots roots, Func<GoalStep> defaultTarget, Func<int> hz, Func<bool> hasKey)
    {
        Game = game;
        this.lib = lib;
        this.steamStatus = steamStatus;
        this.onHdrChanged = onHdrChanged;
        this.roots = roots;
        this.defaultTarget = defaultTarget;
        this.hz = hz;
        this.hasKey = hasKey;
        LoadAdvice();
        ExeCandidates = game.Source == GameSource.Folder ? Scan.MainExe.Candidates(game.InstallDir)
            : game.MainExe != null ? [game.MainExe] : Array.Empty<string>();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Game Game { get; }
    public string Name => Game.Name;
    public string Initial => Name.Length > 0 ? char.ToUpperInvariant(Name[0]).ToString() : "?";
    public string Source => Game.Source.ToString();
    public IReadOnlyList<string> ExeCandidates { get; }
    public bool CanAddToSteam => Game.Source is GameSource.Folder or GameSource.App;
    public bool HdrApplies => Game.Source != GameSource.App;
    public bool SteamApplies => steamStatus != null;
    public bool SteamAdded => steamStatus is Steam.SteamStatus.Added or Steam.SteamStatus.NoArtwork;

    public bool HdrEnabled
    {
        get => !lib.Overrides.TryGetValue(Game.InstallDir, out var o) || o.HdrEnabled;
        set
        {
            lib.For(Game).HdrEnabled = value;
            Save();
            onHdrChanged();
            PropertyChanged?.Invoke(this, new(nameof(HdrEnabled)));
        }
    }

    public bool Hidden
    {
        get => lib.Overrides.TryGetValue(Game.InstallDir, out var o) && o.Hidden;
        set
        {
            if (value == Hidden) return;
            lib.For(Game).Hidden = value;
            Save();
            onHdrChanged();
            PropertyChanged?.Invoke(this, new(nameof(Hidden)));
            HiddenChanged?.Invoke();
        }
    }

    // Lets the window re-filter the grid.
    public event Action? HiddenChanged;

    public string? MainExe
    {
        get => lib.MainExeOf(Game);
        set
        {
            var exe = string.IsNullOrWhiteSpace(value) ? null : value;
            if (exe == lib.MainExeOf(Game)) return;
            lib.For(Game).MainExe = exe;
            Save();
        }
    }

    public string SearchName
    {
        get => lib.SearchNameOf(Game);
        set
        {
            var name = string.IsNullOrWhiteSpace(value) || value == Game.Name ? null : value;
            if ((name ?? Game.Name) == lib.SearchNameOf(Game)) return;
            lib.For(Game).SearchName = name;
            Save();
        }
    }

    bool hltbLoading;

    public HltbInfo? Hltb
    {
        get => lib.For(Game).Hltb;
        set
        {
            lib.For(Game).Hltb = value;
            Save();
            NotifyHltb();
        }
    }

    public bool HltbLoading
    {
        get => hltbLoading;
        set
        {
            hltbLoading = value;
            NotifyHltb();
        }
    }

    public string HltbText => HltbLoading ? "HLTB: …" : Hltb is { } h ? HltbClient.Format(h) : "";
    public bool HltbVisible => HltbLoading || Hltb != null;
    public string HltbTip => Hltb is { Id: not null } h ? $"{h.Name} на HowLongToBeat" : "Поменяйте «Имя для поиска» в параметрах";

    void NotifyHltb()
    {
        foreach (var n in new[] { nameof(Hltb), nameof(HltbText), nameof(HltbVisible), nameof(HltbTip) })
            PropertyChanged?.Invoke(this, new(n));
    }

    public SteamStatus? SteamStatus
    {
        get => steamStatus;
        set
        {
            steamStatus = value;
            PropertyChanged?.Invoke(this, new(nameof(SteamStatus)));
            PropertyChanged?.Invoke(this, new(nameof(SteamText)));
            PropertyChanged?.Invoke(this, new(nameof(Details)));
            PropertyChanged?.Invoke(this, new(nameof(SteamApplies)));
            PropertyChanged?.Invoke(this, new(nameof(SteamAdded)));
        }
    }

    public string SteamText => steamStatus switch
    {
        Steam.SteamStatus.NotAdded => "нет",
        Steam.SteamStatus.Added => "добавлена",
        Steam.SteamStatus.NoArtwork => "без обложки",
        _ => "",
    };

    public string Details => SteamText.Length == 0 ? Source : $"{Source} · Steam: {SteamText}";

    public ImageSource? Cover
    {
        get => cover;
        set
        {
            cover = value;
            PropertyChanged?.Invoke(this, new(nameof(Cover)));
        }
    }

    public ImageSource? Icon
    {
        get => icon;
        set
        {
            icon = value;
            PropertyChanged?.Invoke(this, new(nameof(Icon)));
        }
    }

    public void SetCover(ImageSource? cover) => Cover = cover;

    GoalStep? TargetOverride => lib.Overrides.TryGetValue(Game.InstallDir, out var o) ? o.Target : null;

    public GoalStep Goal => TargetOverride ?? defaultTarget();

    public bool HasOwnTarget => TargetOverride is not null;

    public int TargetIndex
    {
        get => (int)Goal;
        set
        {
            if (value == (int)Goal) return;
            lib.For(Game).Target = (GoalStep)value;
            SaveSoon();
            NotifyTarget();
        }
    }

    public string TargetText => GoalTargets.Label(Goal, hz(), !HasOwnTarget);

    public void ResetTarget()
    {
        if (!HasOwnTarget) return;
        lib.For(Game).Target = null;
        SaveSoon();
        NotifyTarget();
    }

    void NotifyTarget()
    {
        Notify(nameof(TargetIndex));
        Notify(nameof(TargetText));
        Notify(nameof(HasOwnTarget));
    }

    public Advice.Advice? Latest => latest;
    public bool HasAdvice => latest != null;
    public string AdviceSummary => latest?.Result.Summary ?? "";
    public IReadOnlyList<AdviceRowVm> AdviceRows => adviceRows;
    public bool HasRows => adviceRows.Count > 0;
    public string AdviceHeading => latest == null ? "Текущие настройки:" : Advisor.Edits(latest.Result).Count > 0 ? "Рекомендуемые настройки:" : "Выставить в меню игры:";
    public string AdviceNotes => latest?.Result.Notes ?? "";
    public string AdviceAt => latest is null ? "" : latest.At.ToLocalTime().ToString("dd.MM HH:mm");
    public bool CanApply => canApply;
    public bool HasKey => hasKey();
    public bool NoKey => !hasKey();
    public bool CanAsk => hasKey() && !isAsking;
    public bool CanLocate => hasKey() && !isAsking;
    public bool CanChat => HasAdvice && hasKey() && !isAsking;
    public bool WasApplied => wasApplied;
    public IReadOnlyList<ChatTurnVm> Chat => chat;
    public bool IsIdle => !isAsking;
    public bool CanApplyNow => canApply && !isAsking;
    public string ConfigText => configText;
    public string ConfigDetail => configDetail;
    public bool HasConfigPath => hasConfigPath;
    public bool ConfigMissing => configMissing;
    public bool CanRestore => canRestore;
    public bool SnapshotStale => lib.Overrides.TryGetValue(Game.InstallDir, out var o) && o.Snapshot != null
        && Advisor.LocatedPath(lib, roots, Game) is { } p && File.Exists(p) && !Advisor.IsSnapshotFresh(lib, roots, Game);
    public bool Located => lib.Overrides.TryGetValue(Game.InstallDir, out var o) && (o.ConfigPath != null || o.ConfigNote != null);

    public bool IsAsking
    {
        get => isAsking;
        set
        {
            isAsking = value;
            Notify(nameof(IsAsking));
            foreach (var name in new[] { nameof(CanAsk), nameof(CanLocate), nameof(IsIdle), nameof(CanApplyNow), nameof(CanChat) })
                Notify(name);
        }
    }

    public string AskStage
    {
        get => askStage;
        set
        {
            askStage = value;
            Notify(nameof(AskStage));
        }
    }

    public string AdviceStatus
    {
        get => adviceStatus;
        set
        {
            adviceStatus = value;
            Notify(nameof(AdviceStatus));
        }
    }

    public string ApplyStatus
    {
        get => applyStatus;
        set
        {
            applyStatus = value;
            Notify(nameof(ApplyStatus));
        }
    }

    public void LoadAdvice()
    {
        latest = lib.Overrides.TryGetValue(Game.InstallDir, out var o) ? o.Advice : null;
        canApply = false;
        provisional = null;
        chat = (o?.Chat ?? []).Select(t => new ChatTurnVm(t.Role == ChatRole.User, t.Text,
            t.Attachments is { Count: > 0 } a ? "📎 " + string.Join(", ", a) : "", t.At.ToLocalTime().ToString("dd.MM HH:mm"))).ToList();
        adviceRows = []; // rebuilt by RefreshCanApply/RefreshConfig: BuildRows touches the file system
        foreach (var name in new[] { nameof(HasRows), nameof(SnapshotStale), nameof(Latest), nameof(HasAdvice), nameof(AdviceSummary), nameof(AdviceRows), nameof(AdviceHeading),nameof(AdviceNotes), nameof(AdviceAt), nameof(CanApply), nameof(CanApplyNow), nameof(CanChat), nameof(Chat), nameof(TargetIndex), nameof(TargetText), nameof(HasOwnTarget) })
            Notify(name);
    }

    // Reads the config file, so it runs when the row is selected or its advice changes, not for every row at startup.
    public void RefreshCanApply()
    {
        canApply = false;
        if (latest != null && Advisor.Edits(latest.Result) is { Count: > 0 } edits)
        {
            try
            {
                canApply = Advisor.EditsValid(edits)
                    && Advisor.LocatedPath(lib, roots, Game) is { } path
                    && File.Exists(path)
                    && Advisor.CanWrite(File.ReadAllText(path), edits);
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { }
        }
        adviceRows = BuildRows();
        Notify(nameof(CanApply));
        Notify(nameof(CanApplyNow));
        Notify(nameof(AdviceRows));
    }

    // Touches the file system, so it runs when the page opens and after locate/ask/apply.
    public void RefreshConfig()
    {
        var path = Advisor.LocatedPath(lib, roots, Game);
        var note = lib.Overrides.TryGetValue(Game.InstallDir, out var o) ? o.ConfigNote ?? "" : "";
        hasConfigPath = path != null;
        configMissing = path != null && !File.Exists(path);
        canRestore = path != null && File.Exists(path + ".orig");
        wasApplied = path != null && File.Exists(path + ".bak");
        (configText, configDetail) = path != null
            ? configMissing
                ? ("Конфиг ещё не создан: запустите игру один раз, выйдите и нажмите «Подобрать» снова", path)
                : ($"Конфиг: {path}", "")
            : Located ? ("У игры нет текстового конфига — настройки только для меню игры", note)
            : ("Конфиг: будет найден при подборе", "");
        adviceRows = BuildRows();
        foreach (var name in new[] { nameof(ConfigText), nameof(ConfigDetail), nameof(HasConfigPath), nameof(ConfigMissing), nameof(CanRestore), nameof(WasApplied), nameof(SnapshotStale), nameof(HasRows), nameof(AdviceRows), nameof(AdviceHeading) })
            Notify(name);
    }

    // Rows parsed locally from the file while the model request still runs.
    public void ShowProvisional(IReadOnlyList<CurrentSetting> rows)
    {
        provisional = rows;
        adviceRows = BuildRows();
        foreach (var name in new[] { nameof(HasRows), nameof(AdviceRows), nameof(AdviceHeading) })
            Notify(name);
    }

    List<AdviceRowVm> BuildRows()
    {
        var snapshot = lib.Overrides.TryGetValue(Game.InstallDir, out var o) ? o.Snapshot : null;
        var current = latest != null
            ? Advisor.IsSnapshotFresh(lib, roots, Game) ? snapshot!.Settings : null
            : (Advisor.LocatedPath(lib, roots, Game) is { } p && File.Exists(p) ? snapshot?.Settings : null) ?? provisional;
        return AdviceTable.Build(latest, current);
    }

    public void NotifyDefaultsChanged()
    {
        foreach (var name in new[] { nameof(TargetIndex), nameof(TargetText), nameof(HasOwnTarget), nameof(HasKey), nameof(NoKey), nameof(CanAsk), nameof(CanLocate), nameof(CanChat) })
            Notify(name);
    }

    void Notify(string name) => PropertyChanged?.Invoke(this, new(name));

    void Save() => lib.Save(AppPaths.LibraryFile);

    DispatcherTimer? saveTimer;

    // Dragging the goal slider changes the goal per tick; the file is written once it pauses.
    void SaveSoon()
    {
        saveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background,
            (_, _) => { saveTimer!.Stop(); Save(); }, Dispatcher.CurrentDispatcher);
        saveTimer.Stop();
        saveTimer.Start();
    }
}

public sealed record ChatTurnVm(bool Mine, string Text, string Attachments, string At);
