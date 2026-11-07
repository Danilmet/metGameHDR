using System.ComponentModel;
using System.IO;
using System.Windows.Media;
using MetGameHDR.Advice;
using MetGameHDR.Scan;
using MetGameHDR.Steam;

namespace MetGameHDR.Ui;

public sealed record AdviceRowVm(string Name, string Current, string Recommended, string Reason, bool IsConfig);

public sealed class GameRow : INotifyPropertyChanged
{
    readonly Library lib;
    readonly Action onHdrChanged;
    SteamStatus? steamStatus;
    ImageSource? cover;
    ImageSource? icon;
    readonly FinderRoots roots;
    readonly Func<GraphicsGoal> defaultGoal;
    readonly Func<bool> hasKey;
    Advice.Advice? latest;
    bool canApply;
    bool isAsking;
    string adviceStatus = "";
    string applyStatus = "";
    IReadOnlyList<AdviceRowVm> adviceRows = [];

    public GameRow(Game game, Library lib, SteamStatus? steamStatus, Action onHdrChanged,
        FinderRoots roots, Func<GraphicsGoal> defaultGoal, Func<bool> hasKey)
    {
        Game = game;
        this.lib = lib;
        this.steamStatus = steamStatus;
        this.onHdrChanged = onHdrChanged;
        this.roots = roots;
        this.defaultGoal = defaultGoal;
        this.hasKey = hasKey;
        LoadAdvice();
        ExeCandidates = game.Source == GameSource.Folder ? Scan.MainExe.Candidates(game.InstallDir)
            : game.MainExe != null ? [game.MainExe] : Array.Empty<string>();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Game Game { get; }
    public string Name => Game.Name;
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

    GraphicsGoal? GoalOverride => lib.Overrides.TryGetValue(Game.InstallDir, out var o) ? o.Goal : null;

    public GraphicsGoal Goal => GoalOverride ?? defaultGoal();

    public string GoalFps
    {
        get => GoalOverride?.Fps.ToString() ?? "";
        set
        {
            var text = value.Trim();
            if (text.Length == 0)
            {
                if (GoalOverride is null) return;
                lib.For(Game).Goal = null;
                Save();
                Notify(nameof(GoalPriority));
                return;
            }
            if (!int.TryParse(text, out var fps) || fps <= 0 || fps == GoalOverride?.Fps) return;
            lib.For(Game).Goal = new GraphicsGoal(fps, Goal.Priority);
            Save();
        }
    }

    public string FpsHint => defaultGoal().Fps.ToString();

    public Priority GoalPriority
    {
        get => Goal.Priority;
        set
        {
            if (value == Goal.Priority) return;
            lib.For(Game).Goal = new GraphicsGoal(Goal.Fps, value);
            Save();
            Notify(nameof(GoalFps));
        }
    }

    public Advice.Advice? Latest => latest;
    public bool HasAdvice => latest != null;
    public string AdviceSummary => latest?.Result.Summary ?? "";
    public IReadOnlyList<AdviceRowVm> AdviceRows => adviceRows;
    public string AdviceHeading => latest?.Result.Config is { Edits.Count: > 0 } ? "Рекомендуемые настройки:" : "Выставить в меню игры:";
    public string OptimizedText => latest is null ? "Настройки не подобраны" : $"Настройки подобраны {AdviceAt}";
    public string AdviceNotes => latest?.Result.Notes ?? "";
    public string AdviceAt => latest is null ? "" : latest.At.ToLocalTime().ToString("dd.MM HH:mm");
    public bool CanApply => canApply;
    public bool HasKey => hasKey();
    public bool NoKey => !hasKey();
    public bool CanAsk => hasKey() && !isAsking;

    public bool IsAsking
    {
        get => isAsking;
        set
        {
            isAsking = value;
            Notify(nameof(IsAsking));
            Notify(nameof(CanAsk));
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
        adviceRows = [];
        foreach (var name in new[] { nameof(Latest), nameof(HasAdvice), nameof(AdviceSummary), nameof(AdviceRows), nameof(AdviceHeading), nameof(OptimizedText), nameof(AdviceNotes), nameof(AdviceAt), nameof(CanApply), nameof(GoalFps), nameof(GoalPriority) })
            Notify(name);
    }

    // Reads config files, so it runs when the row is selected or its advice changes, not for every row at startup.
    public void RefreshCanApply()
    {
        canApply = false;
        if (latest?.Result.Config is { Edits.Count: > 0 } config)
        {
            try
            {
                canApply = Advisor.EditsValid(config)
                    && Advisor.IsCandidate(Advisor.EffectiveGame(lib, Game), roots, config.Path)
                    && File.Exists(config.Path)
                    && Advisor.CanWrite(File.ReadAllText(config.Path), config.Edits);
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { }
        }
        adviceRows = BuildRows();
        Notify(nameof(CanApply));
        Notify(nameof(AdviceRows));
    }

    // The config file is read only once it passed the candidate check; otherwise the model-supplied path stays untouched.
    List<AdviceRowVm> BuildRows()
    {
        var rows = new List<AdviceRowVm>();
        if (latest is null) return rows;
        if (latest.Result.Config is { } config)
        {
            var edits = canApply
                ? Advisor.Preview(latest).Select(p => (p.Edit, Current: p.Current ?? "нет"))
                : config.Edits.Select(e => (Edit: e, Current: "—"));
            foreach (var (edit, current) in edits)
                rows.Add(new(edit.Section is null ? edit.Key : $"[{edit.Section}] {edit.Key}", current, edit.Value, "", true));
        }
        rows.AddRange(latest.Result.Settings.Select(s => new AdviceRowVm(s.Name, "—", s.Value, s.Reason, false)));
        return rows;
    }

    public void NotifyDefaultsChanged()
    {
        foreach (var name in new[] { nameof(FpsHint), nameof(GoalPriority), nameof(HasKey), nameof(NoKey), nameof(CanAsk) })
            Notify(name);
    }

    void Notify(string name) => PropertyChanged?.Invoke(this, new(name));

    void Save() => lib.Save(AppPaths.LibraryFile);
}
