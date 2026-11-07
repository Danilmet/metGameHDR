using System.ComponentModel;
using MetGameHDR.Scan;
using MetGameHDR.Steam;

namespace MetGameHDR.Ui;

public sealed class GameRow : INotifyPropertyChanged
{
    readonly Library lib;
    SteamStatus? steamStatus;

    public GameRow(Game game, Library lib, SteamStatus? steamStatus)
    {
        Game = game;
        this.lib = lib;
        this.steamStatus = steamStatus;
        ExeCandidates = game.Source == GameSource.Folder ? Scan.MainExe.Candidates(game.InstallDir)
            : game.MainExe != null ? [game.MainExe] : Array.Empty<string>();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Game Game { get; }
    public string Name => Game.Name;
    public string Source => Game.Source.ToString();
    public IReadOnlyList<string> ExeCandidates { get; }
    public bool CanAddToSteam => Game.Source is GameSource.Folder or GameSource.App;

    public bool HdrEnabled
    {
        get => !lib.Overrides.TryGetValue(Game.InstallDir, out var o) || o.HdrEnabled;
        set { lib.For(Game).HdrEnabled = value; Save(); }
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
            lib.For(Game).SearchName = string.IsNullOrWhiteSpace(value) ? null : value;
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
        }
    }

    public string SteamText => steamStatus switch
    {
        Steam.SteamStatus.NotAdded => "нет",
        Steam.SteamStatus.Added => "добавлена",
        Steam.SteamStatus.NoArtwork => "без обложки",
        _ => "",
    };

    void Save() => lib.Save(AppPaths.LibraryFile);
}
