using System.ComponentModel;
using System.Windows.Media;
using MetGameHDR.Scan;
using MetGameHDR.Steam;

namespace MetGameHDR.Ui;

public sealed class GameRow : INotifyPropertyChanged
{
    readonly Library lib;
    readonly Action onHdrChanged;
    SteamStatus? steamStatus;
    ImageSource? cover;
    ImageSource? icon;

    public GameRow(Game game, Library lib, SteamStatus? steamStatus, Action onHdrChanged)
    {
        Game = game;
        this.lib = lib;
        this.steamStatus = steamStatus;
        this.onHdrChanged = onHdrChanged;
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
        }
    }

    public string SteamText => steamStatus switch
    {
        Steam.SteamStatus.NotAdded => "нет",
        Steam.SteamStatus.Added => "добавлена",
        Steam.SteamStatus.NoArtwork => "без обложки",
        _ => "",
    };

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

    void Save() => lib.Save(AppPaths.LibraryFile);
}
