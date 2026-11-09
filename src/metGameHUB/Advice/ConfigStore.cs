using System.IO;

namespace MetGameHUB.Advice;

// Config access by path: a file, or a registry key (Unity PlayerPrefs, "HKCU\..."); the Advisor goes through here.
public static class ConfigStore
{
    internal static Func<string> BackupDir = () => AppPaths.RegistryDir;

    public static bool IsRegistry(string path) =>
        path.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase) || path.StartsWith(@"HKEY_CURRENT_USER\", StringComparison.OrdinalIgnoreCase);

    static string Orig(string key) => Path.Combine(BackupDir(), RegistryConfig.BackupFileName(key) + ".orig.reg");

    static string Bak(string key) => Path.Combine(BackupDir(), RegistryConfig.BackupFileName(key) + ".bak.reg");

    public static bool Exists(string path) => IsRegistry(path) ? RegistryConfig.Exists(path) : File.Exists(path);

    public static DateTime Stamp(string path) => IsRegistry(path) ? RegistryConfig.Stamp(path) : File.GetLastWriteTimeUtc(path);

    public static string? ReadText(string path)
    {
        if (IsRegistry(path)) return RegistryConfig.Exists(path) ? RegistryConfig.Dump(path) : null;
        try { return File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // '<' (XML) is refused; '{' is accepted only as line JSON where every edited key already exists.
    public static bool CanWrite(string path, string text, IReadOnlyList<ConfigEdit> edits)
    {
        if (IsRegistry(path)) return true;
        var first = text.TrimStart('﻿', ' ', '\t', '\r', '\n', '\0').FirstOrDefault();
        if (first == '<') return false;
        return first != '{' || edits.All(e => IniEditor.CurrentValue(text, null, e.Key) != null);
    }

    public static bool Apply(string path, IReadOnlyList<ConfigEdit> edits)
    {
        if (IsRegistry(path))
        {
            if (RegistryConfig.Prepare(path, edits) is not { } writes) return false;
            if (!File.Exists(Orig(path))) RegistryConfig.Backup(path, Orig(path));
            RegistryConfig.Backup(path, Bak(path));
            RegistryConfig.Write(path, writes);
            return true;
        }
        var original = File.ReadAllBytes(path);
        if (IniEditor.ApplyBytes(original, edits) is not { } updated)
        {
            Log.Write($"Apply advice: value does not fit the ANSI encoding of {path}");
            return false;
        }
        if (!File.Exists(path + ".orig")) File.WriteAllBytes(path + ".orig", original);
        File.WriteAllBytes(path + ".bak", original);

        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, updated);
        try { File.Move(tmp, path, overwrite: true); }
        catch { File.Delete(tmp); throw; }
        return true;
    }

    public static bool HasBackup(string path) => File.Exists(IsRegistry(path) ? Orig(path) : path + ".orig");

    public static bool WasApplied(string path) => File.Exists(IsRegistry(path) ? Bak(path) : path + ".bak");

    public static void Restore(string path)
    {
        if (IsRegistry(path))
        {
            if (RegistryConfig.Exists(path)) RegistryConfig.Backup(path, Bak(path));
            RegistryConfig.Restore(Orig(path));
            return;
        }
        if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
        var tmp = path + ".tmp";
        File.Copy(path + ".orig", tmp, overwrite: true);
        try { File.Move(tmp, path, overwrite: true); }
        catch { File.Delete(tmp); throw; }
    }

    public static string? CurrentValue(string path, string text, string? section, string key) =>
        IsRegistry(path) ? RegistryConfig.CurrentValue(path, key) : IniEditor.CurrentValue(text, section, key);
}
