using System.IO;
using System.Text.Json;

namespace MetGameHUB.Scan;

public sealed class EpicSource(string manifestsDir) : IGameSource
{
    public EpicSource() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        @"Epic\EpicGamesLauncher\Data\Manifests")) { }

    public GameSource Source => GameSource.Epic;

    public IEnumerable<Game> Scan()
    {
        if (!Directory.Exists(manifestsDir)) yield break;
        foreach (var file in Directory.EnumerateFiles(manifestsDir, "*.item"))
        {
            var game = Parse(file);
            if (game != null) yield return game;
        }
    }

    static Game? Parse(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            var name = Str(root, "DisplayName");
            var dir = Str(root, "InstallLocation");
            var exe = Str(root, "LaunchExecutable");
            if (name == null || dir == null || exe == null) return null;
            dir = Path.TrimEndingDirectorySeparator(dir);
            return new Game(name, dir, GameSource.Epic, Path.Combine(dir, exe));
        }
        catch (Exception e) when (e is JsonException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    static string? Str(JsonElement root, string prop) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;
}
