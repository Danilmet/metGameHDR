using System.IO;

namespace MetGameHDR.Steam;

public static class ShortcutsFile
{
    static readonly char[] Seps = ['\\', '/'];

    public static VdfObject Load(string path)
    {
        if (File.Exists(path)) return BinaryVdf.Read(File.ReadAllBytes(path));
        return new VdfObject { new("shortcuts", new VdfObject()) };
    }

    public static void Save(string path, VdfObject root)
    {
        if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, BinaryVdf.Write(root));
        File.Move(tmp, path, overwrite: true);
    }

    static IEnumerable<VdfObject> Entries(VdfObject root) =>
        root["shortcuts"] is VdfObject s ? s.Select(p => p.Value).OfType<VdfObject>() : [];

    static string NormDir(string? s) => (s ?? "").Trim().Trim('"').TrimEnd(Seps);

    static bool SameDir(string? a, string? b) =>
        string.Equals(NormDir(a), NormDir(b), StringComparison.OrdinalIgnoreCase);

    public static VdfObject? Find(VdfObject root, string startDir) =>
        Entries(root).FirstOrDefault(e => SameDir(e["StartDir"] as string, startDir));

    public static uint AppIdOf(VdfObject entry) =>
        entry["appid"] is int id
            ? unchecked((uint)id)
            : ShortcutAppId.Compute(entry["Exe"] as string ?? "", entry["AppName"] as string ?? "");

    public static VdfObject Upsert(VdfObject root, string appName, string exePath, string startDir, string icon)
    {
        var exeQuoted = "\"" + exePath + "\"";
        var entry = Find(root, startDir);
        var matchedByDir = entry != null;
        entry ??= Entries(root).FirstOrDefault(e =>
            string.Equals(((e["Exe"] as string) ?? "").Trim().Trim('"').Trim(), exePath, StringComparison.OrdinalIgnoreCase));
        if (entry != null)
        {
            entry["AppName"] = appName;
            entry["Exe"] = exeQuoted;
            if (!matchedByDir) entry["StartDir"] = startDir;
            entry["icon"] = icon;
            return entry;
        }

        if (root["shortcuts"] is not VdfObject shortcuts)
        {
            shortcuts = new VdfObject();
            root["shortcuts"] = shortcuts;
        }
        var next = shortcuts.Select(p => int.TryParse(p.Key, out var n) ? n : -1).DefaultIfEmpty(-1).Max() + 1;
        entry = new VdfObject
        {
            new("appid", unchecked((int)ShortcutAppId.Compute(exeQuoted, appName))),
            new("AppName", appName),
            new("Exe", exeQuoted),
            new("StartDir", startDir),
            new("icon", icon),
            new("ShortcutPath", ""),
            new("LaunchOptions", ""),
            new("IsHidden", 0),
            new("AllowDesktopConfig", 1),
            new("AllowOverlay", 1),
            new("OpenVR", 0),
            new("Devkit", 0),
            new("DevkitGameID", ""),
            new("DevkitOverrideAppID", 0),
            new("LastPlayTime", 0),
            new("FlatpakAppID", ""),
            new("tags", new VdfObject()),
        };
        shortcuts.Add(new(next.ToString(), entry));
        return entry;
    }
}
