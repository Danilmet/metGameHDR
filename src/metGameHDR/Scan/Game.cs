namespace MetGameHDR.Scan;

public enum GameSource { Steam, Epic, Gog, Ubisoft, Ea, Xbox, Folder, App }

public sealed record Game(string Name, string InstallDir, GameSource Source, string? MainExe);

public interface IGameSource
{
    GameSource Source { get; }
    IEnumerable<Game> Scan();
}
