using System.Diagnostics;
using System.IO;

namespace MetGameHDR.Scan;

public sealed class AppsSource(IEnumerable<string> exePaths) : IGameSource
{
    public GameSource Source => GameSource.App;

    public IEnumerable<Game> Scan()
    {
        foreach (var exe in exePaths)
        {
            if (!File.Exists(exe)) continue;
            var product = FileVersionInfo.GetVersionInfo(exe).ProductName;
            var name = string.IsNullOrWhiteSpace(product) ? Path.GetFileNameWithoutExtension(exe) : product;
            yield return new Game(name, Path.GetDirectoryName(exe)!, GameSource.App, exe);
        }
    }
}
