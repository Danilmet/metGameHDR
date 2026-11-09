using System.IO;
using System.Text.Json;

namespace MetGameHUB;

public sealed class Settings
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public List<string> FolderRoots { get; set; } = [];
    public List<string> Apps { get; set; } = [];
    public string? SteamGridDbKey { get; set; }
    public string? SteamUserId { get; set; }
    public bool Autostart { get; set; }
    public bool TurnOffHdrOnExit { get; set; } = true;
    public string? ClaudeApiKey { get; set; }
    public string? ClaudeBaseUrl { get; set; }
    public string? ClaudeModel { get; set; }
    public Advice.GoalStep DefaultTarget { get; set; } = Advice.GoalStep.Balanced;

    public static Settings Load(string path)
    {
        if (!File.Exists(path)) return new Settings();
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Options) ?? new Settings(); }
        catch (Exception e) when (e is JsonException or IOException)
        {
            SetAside(path, e);
            return new Settings();
        }
    }

    public void Save(string path) => WriteAtomic(path, JsonSerializer.Serialize(this, Options));

    internal static void SetAside(string path, Exception error)
    {
        Log.Write($"{Path.GetFileName(path)} unreadable, using defaults: {error.Message}");
        try { File.Move(path, path + ".bad", overwrite: true); }
        catch (IOException e) { Log.Write($"{Path.GetFileName(path)}: cannot rename to .bad: {e.Message}"); }
    }

    internal static void WriteAtomic(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }
}
