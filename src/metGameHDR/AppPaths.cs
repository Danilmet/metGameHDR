using System.IO;

namespace MetGameHDR;

public static class AppPaths
{
    public static string Dir
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "metGameHDR");
            Directory.CreateDirectory(dir);
            return dir;
        }
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
