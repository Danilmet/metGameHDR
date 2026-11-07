using MetGameHDR.Scan;

namespace MetGameHDR.Tests.Scan;

public class LibraryTests
{
    [Fact] public void FindByPath_MatchesOnSegmentBoundary()
    {
        var lib = new Library { Games = [new("Foo", @"C:\Games\Foo", GameSource.Folder, null), new("Foobar", @"C:\Games\Foobar", GameSource.Folder, null)] };
        Assert.Equal("Foobar", lib.FindByPath(@"C:\Games\Foobar\bin\x.exe")!.Name);
        Assert.Equal("Foo", lib.FindByPath(@"c:\games\foo\foo.exe")!.Name);
        Assert.Null(lib.FindByPath(@"C:\Games\Foo.exe"));
    }

    [Fact] public void FindByPath_SkipsAppsAndDisabled()
    {
        var app = new Game("Discord", @"C:\D", GameSource.App, @"C:\D\d.exe");
        var off = new Game("Off", @"C:\Off", GameSource.Folder, null);
        var lib = new Library { Games = [app, off] };
        lib.For(off).HdrEnabled = false;
        Assert.Null(lib.FindByPath(@"C:\D\d.exe")); Assert.Null(lib.FindByPath(@"C:\Off\o.exe"));
    }

    [Fact] public void SaveLoad_KeepsOverrides()
    {
        var path = Path.GetTempFileName();
        var lib = new Library { Games = [new("G", @"C:\G", GameSource.Folder, @"C:\G\a.exe")] };
        lib.For(lib.Games[0]).MainExe = @"C:\G\b.exe";
        lib.Save(path);
        var back = Library.Load(path);
        Assert.Equal(@"C:\G\b.exe", back.MainExeOf(back.Games[0]));
    }

    [Fact] public void Load_CorruptFile_ReturnsEmptyLibraryAndSetsFileAside()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        File.WriteAllText(path, """{ "Games": [ {"Name": """);
        try
        {
            Assert.Empty(Library.Load(path).Games);
            Assert.False(File.Exists(path));
            Assert.True(File.Exists(path + ".bad"));
        }
        finally
        {
            File.Delete(path + ".bad");
        }
    }
}
