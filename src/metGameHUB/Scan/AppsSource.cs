using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace MetGameHUB.Scan;

public sealed class AppsSource(IEnumerable<string> paths) : IGameSource
{
    public GameSource Source => GameSource.App;

    public IEnumerable<Game> Scan() => paths.Select(Resolve).OfType<Game>();

    public static Game? Resolve(string path)
    {
        if (!File.Exists(path)) return null;
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var product = FileVersionInfo.GetVersionInfo(path).ProductName;
            var name = string.IsNullOrWhiteSpace(product) ? Path.GetFileNameWithoutExtension(path) : product;
            return new Game(name, Path.GetDirectoryName(path)!, GameSource.App, path);
        }

        string target, args;
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            var shortcut = shell.CreateShortcut(path);
            target = (string)shortcut.TargetPath;
            args = (string)shortcut.Arguments;
        }
        catch (COMException) { return null; }
        if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(target)) return null;
        return new Game(Path.GetFileNameWithoutExtension(path), Path.GetDirectoryName(target)!, GameSource.App, target, args);
    }
}
