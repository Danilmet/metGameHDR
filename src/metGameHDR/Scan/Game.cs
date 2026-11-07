namespace MetGameHDR.Scan;

public enum GameSource { Steam, Epic, Gog, Ubisoft, Ea, Xbox, Folder, App }

public sealed record Game(string Name, string InstallDir, GameSource Source, string? MainExe, string? LaunchOptions = null, string? SteamAppId = null);

public interface IGameSource
{
    GameSource Source { get; }
    IEnumerable<Game> Scan();
}

public sealed class GameOverride
{
    public bool HdrEnabled { get; set; } = true;
    public string? MainExe { get; set; }
    public string? SearchName { get; set; }
    public Advice.GraphicsGoal? Goal { get; set; }
    public Advice.Advice? Advice { get; set; }
    // Absolute path chosen by the model; null = not located yet.
    public string? ConfigPath { get; set; }
    // The model's note from the locate step ("" = located, no note). Located = ConfigPath != null || ConfigNote != null.
    public string? ConfigNote { get; set; }
}
