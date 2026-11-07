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

    static VdfObject Shortcuts(VdfObject root)
    {
        if (root["shortcuts"] is VdfObject s) return s;
        s = new VdfObject();
        root["shortcuts"] = s;
        return s;
    }

    static bool SameDir(string? a, string? b) =>
        string.Equals(a?.TrimEnd(Seps), b?.TrimEnd(Seps), StringComparison.OrdinalIgnoreCase);

    public static VdfObject? Find(VdfObject root, string startDir) =>
        Shortcuts(root).Select(p => p.Value).OfType<VdfObject>().FirstOrDefault(e => SameDir(e["StartDir"] as string, startDir));

    public static uint AppIdOf(VdfObject entry) => unchecked((uint)(int)entry["appid"]!);

    public static VdfObject Upsert(VdfObject root, string appName, string exePath, string startDir, string icon)
    {
        var shortcuts = Shortcuts(root);
        var exeQuoted = "\"" + exePath + "\"";
        var entry = Find(root, startDir)
            ?? shortcuts.Select(p => p.Value).OfType<VdfObject>().FirstOrDefault(e =>
                string.Equals(((e["Exe"] as string) ?? "").Trim('"'), exePath, StringComparison.OrdinalIgnoreCase));
        if (entry != null)
        {
            entry["AppName"] = appName;
            entry["Exe"] = exeQuoted;
            entry["StartDir"] = startDir;
            entry["icon"] = icon;
            return entry;
        }

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
        shortcuts.Add(new(shortcuts.Count.ToString(), entry));
        return entry;
    }
}
