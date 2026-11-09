using System.IO;

namespace MetGameHUB.Advice;

// Config access by path: today only files; the Advisor goes through here so another storage can be added.
public static class ConfigStore
{
    public static bool IsRegistry(string path) => false;

    public static bool Exists(string path) => File.Exists(path);

    public static DateTime Stamp(string path) => File.GetLastWriteTimeUtc(path);

    public static string? ReadText(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // '<' (XML) is refused; '{' is accepted only as line JSON where every edited key already exists.
    public static bool CanWrite(string path, string text, IReadOnlyList<ConfigEdit> edits)
    {
        var first = text.TrimStart('﻿', ' ', '\t', '\r', '\n', '\0').FirstOrDefault();
        if (first == '<') return false;
        return first != '{' || edits.All(e => IniEditor.CurrentValue(text, null, e.Key) != null);
    }

    public static bool Apply(string path, IReadOnlyList<ConfigEdit> edits)
    {
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

    public static bool HasBackup(string path) => File.Exists(path + ".orig");

    public static bool WasApplied(string path) => File.Exists(path + ".bak");

    public static void Restore(string path)
    {
        if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
        var tmp = path + ".tmp";
        File.Copy(path + ".orig", tmp, overwrite: true);
        try { File.Move(tmp, path, overwrite: true); }
        catch { File.Delete(tmp); throw; }
    }

    public static string? CurrentValue(string path, string text, string? section, string key) => IniEditor.CurrentValue(text, section, key);
}
