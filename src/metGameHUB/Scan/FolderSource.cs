using System.IO;

namespace MetGameHUB.Scan;

public sealed class FolderSource(IEnumerable<string> roots) : IGameSource
{
    public GameSource Source => GameSource.Folder;

    public IEnumerable<Game> Scan()
    {
        foreach (var root in roots)
        {
            List<string> dirs;
            try { dirs = Directory.Exists(root) ? Directory.EnumerateDirectories(root).ToList() : []; }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException) { continue; }

            foreach (var dir in dirs)
            {
                var name = Path.GetFileName(dir);
                string? exe;
                try { exe = MainExe.Pick(dir, name); }
                catch (Exception e) when (e is UnauthorizedAccessException or IOException) { continue; }
                yield return new Game(name, dir, GameSource.Folder, exe);
            }
        }
    }
}
