using MetGameHDR.Advice;
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

    [Fact] public void FindByPath_NestedInstallDirWinsOverParent()
    {
        var lib = new Library { Games = [new("Parent", @"C:\Games", GameSource.Folder, null), new("Child", @"C:\Games\Child", GameSource.Folder, null)] };
        Assert.Equal("Child", lib.FindByPath(@"C:\Games\Child\bin\c.exe")!.Name);
        Assert.Equal("Parent", lib.FindByPath(@"C:\Games\Other\o.exe")!.Name);
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

    [Fact] public void Load_LibraryWithoutSteamAppId_Reads()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        File.WriteAllText(path, """{ "Games": [ { "Name": "G", "InstallDir": "C:\\G", "Source": 0, "MainExe": null, "LaunchOptions": null } ] }""");
        try { Assert.Null(Library.Load(path).Games.Single().SteamAppId); }
        finally { File.Delete(path); }
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

    [Fact] public void Save_Load_RoundTripsGoalAndAdvice()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var g = new Game("G", @"C:\G", GameSource.Folder, null);
        var lib = new Library { Games = [g] };
        var at = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var setting = new SettingAdvice("Shadows", "High", "Low", "Sec", "k", "0", "fps");
        lib.For(g).Goal = new GraphicsGoal(120, Priority.Responsive);
        lib.For(g).Advice = new Advice.Advice(new AdviceResult("s", [setting], "n"), at);
        lib.For(g).ConfigPath = @"C:G.ini";
        try
        {
            lib.Save(path);
            var o = Library.Load(path).Overrides[@"C:\G"];
            Assert.Equal(new GraphicsGoal(120, Priority.Responsive), o.Goal);
            Assert.Equal(at, o.Advice!.At);
            Assert.Equal("s", o.Advice.Result.Summary);
            Assert.Equal(setting, o.Advice.Result.Settings.Single());
            Assert.Equal("n", o.Advice.Result.Notes);
            Assert.Equal(@"C:G.ini", o.ConfigPath);
            Assert.Contains("\"Responsive\"", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact] public void Load_OldFileWithoutAdviceFields_ReadsDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        File.WriteAllText(path, """{ "Overrides": { "C:\\G": { "HdrEnabled": false } } }""");
        try
        {
            var o = Library.Load(path).Overrides[@"C:\G"];
            Assert.False(o.HdrEnabled);
            Assert.Null(o.Goal);
            Assert.Null(o.Advice);
        }
        finally { File.Delete(path); }
    }

    [Fact] public void Load_OldAdviceShape_DoesNotThrow()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        File.WriteAllText(path, """
            { "Overrides": { "C:\\G": { "Advice": { "At": "2026-01-02T03:04:05Z", "Result": {
              "Summary": "s", "Notes": "n",
              "Settings": [ { "Name": "Shadows", "Value": "Low", "Reason": "r" } ],
              "Config": { "Path": "a.ini", "Edits": [ { "Section": null, "Key": "k", "Value": "v" } ] } } } } } }
            """);
        try
        {
            var o = Library.Load(path).Overrides[@"C:\G"];
            Assert.Null(o.ConfigPath);
            Assert.Equal("s", o.Advice!.Result.Summary);
            var s = Assert.Single(o.Advice.Result.Settings);
            Assert.Equal("Shadows", s.Name);
            Assert.Null(s.Current);
            Assert.Null(s.Key);
            Assert.Equal("r", s.Reason);
        }
        finally { File.Delete(path); }
    }
}
