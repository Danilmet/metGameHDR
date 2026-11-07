using System.IO;
using Microsoft.Win32;

namespace MetGameHDR.Scan;

internal static class RegistryHelper
{
    public static IEnumerable<(string Name, RegistryKey Key)> SubKeys(string path)
    {
        using var root = Registry.LocalMachine.OpenSubKey(path);
        if (root is null) yield break;
        foreach (var name in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(name);
            if (key is not null) yield return (name, key);
        }
    }
}

public sealed class GogSource : IGameSource
{
    public GameSource Source => GameSource.Gog;

    public IEnumerable<Game> Scan()
    {
        foreach (var (id, key) in RegistryHelper.SubKeys(@"SOFTWARE\WOW6432Node\GOG.com\Games"))
        {
            var path = key.GetValue("path") as string;
            if (string.IsNullOrWhiteSpace(path)) continue;
            yield return new Game(key.GetValue("gameName") as string ?? id, path, GameSource.Gog, null);
        }
    }
}

public sealed class UbisoftSource : IGameSource
{
    public GameSource Source => GameSource.Ubisoft;

    public IEnumerable<Game> Scan()
    {
        foreach (var (_, key) in RegistryHelper.SubKeys(@"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs"))
        {
            var dir = (key.GetValue("InstallDir") as string)?.Replace('/', '\\').TrimEnd('\\');
            if (string.IsNullOrEmpty(dir)) continue;
            yield return new Game(Path.GetFileName(dir), dir, GameSource.Ubisoft, null);
        }
    }
}

public sealed class EaSource : IGameSource
{
    public GameSource Source => GameSource.Ea;

    public IEnumerable<Game> Scan()
    {
        foreach (var root in new[] { @"SOFTWARE\WOW6432Node\EA Games", @"SOFTWARE\WOW6432Node\Electronic Arts" })
        foreach (var (name, key) in RegistryHelper.SubKeys(root))
        {
            var dir = key.GetValue("Install Dir") as string;
            if (string.IsNullOrWhiteSpace(dir)) continue;
            yield return new Game(key.GetValue("DisplayName") as string ?? name, dir, GameSource.Ea, null);
        }
    }
}
