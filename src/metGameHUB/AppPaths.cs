using System.IO;

namespace MetGameHUB;

public static class AppPaths
{
    public static string Dir
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "metGameHUB");
            MoveLegacy(Path.Combine(appData, "metGameHDR"), dir);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    // one-time move after the rename from metGameHDR: otherwise settings and the library would "disappear"
    internal static void MoveLegacy(string oldDir, string newDir)
    {
        if (Directory.Exists(oldDir) && !Directory.Exists(newDir)) Directory.Move(oldDir, newDir);
    }

    public static string SettingsFile => Path.Combine(Dir, "settings.json");
    public static string LibraryFile => Path.Combine(Dir, "library.json");
    public static string LogFile => Path.Combine(Dir, "log.txt");

    public static string CoversDir
    {
        get
        {
            var dir = Path.Combine(Dir, "covers");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string UpdateDir => Path.Combine(Dir, "update");
}
