using System.IO;

namespace MetGameHDR;

public static class Log
{
    const long MaxBytes = 1024 * 1024;
    static readonly object Gate = new();

    public static void Write(string message)
    {
        lock (Gate)
        {
            var file = AppPaths.LogFile;
            if (File.Exists(file) && new FileInfo(file).Length > MaxBytes)
                File.Move(file, Path.Combine(AppPaths.Dir, "log.old"), overwrite: true);
            File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
    }
}
