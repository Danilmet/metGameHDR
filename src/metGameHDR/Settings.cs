using System.IO;
using System.Text.Json;

namespace MetGameHDR;

public sealed class Settings
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public List<string> FolderRoots { get; set; } = [];
    public List<string> Apps { get; set; } = [];
    public string? SteamGridDbKey { get; set; }
    public string? SteamUserId { get; set; }
    public bool Autostart { get; set; }
    public bool TurnOffHdrOnExit { get; set; } = true;

    public static Settings Load(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Options) ?? new Settings()
            : new Settings();

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
}
