using System.IO;

namespace MetGameHDR.Scan;

public sealed class FolderSource(IEnumerable<string> roots) : IGameSource
{
    public GameSource Source => GameSource.Folder;

    public IEnumerable<Game> Scan()
    {
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(dir);
                var exe = MainExe.Pick(dir, name);
                if (exe is not null) yield return new Game(name, dir, GameSource.Folder, exe);
            }
        }
    }
}
