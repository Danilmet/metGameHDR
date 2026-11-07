using System.IO;
using MetGameHDR.Steam;

namespace MetGameHDR.Tests.Steam;

public class ShortcutsFileTests
{
    [Fact] public void Crc32_KnownVector() => Assert.Equal(891568578u, ShortcutAppId.Crc32("abc"));
    [Fact] public void Compute_SetsHighBit() => Assert.Equal(3039052226u, ShortcutAppId.Compute("a", "bc"));

    [Fact] public void Upsert_AddsNewEntryWithAllFields()
    {
        var root = ShortcutsFile.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".vdf"));
        ShortcutsFile.Upsert(root, "Game", @"C:\G\g.exe", @"C:\G", "");
        var e = ShortcutsFile.Find(root, @"c:\g")!;
        Assert.Equal("\"C:\\G\\g.exe\"", e["Exe"]); Assert.Equal(2228080894u, ShortcutsFile.AppIdOf(e)); Assert.Equal(1, e["AllowOverlay"]); Assert.IsType<VdfObject>(e["tags"]);
    }

    [Fact] public void Upsert_KeepsExistingAppIdAndFields()
    {
        var root = BinaryVdf.Read(BinaryVdfTests.Fixture());
        ShortcutsFile.Upsert(root, "Game Renamed", @"C:\G\other.exe", @"C:\G", "i.png");
        var e = ShortcutsFile.Find(root, @"C:\G")!;
        Assert.Equal(0xE404CA14u, ShortcutsFile.AppIdOf(e)); Assert.Equal("Game Renamed", e["AppName"]); Assert.Equal(0x04030201, e["LastPlayTime"]);
        Assert.Single((VdfObject)root["shortcuts"]!);
    }

    [Fact] public void Upsert_WritesLaunchOptionsOnInsertAndUpdate()
    {
        var root = ShortcutsFile.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".vdf"));
        var e = ShortcutsFile.Upsert(root, "Discord", @"C:\D\Update.exe", @"C:\D", "", "--processStart Discord.exe");
        Assert.Equal("--processStart Discord.exe", e["LaunchOptions"]);
        ShortcutsFile.Upsert(root, "Discord", @"C:\D\Update.exe", @"C:\D", "", "-x");
        Assert.Equal("-x", e["LaunchOptions"]);
    }

    [Fact] public void Save_WritesBackupAndAtomically()
    {
        var path = Path.GetTempFileName(); File.WriteAllBytes(path, BinaryVdfTests.Fixture());
        var root = ShortcutsFile.Load(path); ShortcutsFile.Upsert(root, "N", @"C:\N\n.exe", @"C:\N", "");
        ShortcutsFile.Save(path, root);
        Assert.Equal(BinaryVdfTests.Fixture(), File.ReadAllBytes(path + ".bak"));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal(2, ((VdfObject)ShortcutsFile.Load(path)["shortcuts"]!).Count);
        File.Delete(path); File.Delete(path + ".bak");
    }

    static VdfObject Entry(string? appId, string startDir, string exe = "\"C:\\G\\g.exe\"")
    {
        var e = new VdfObject();
        if (appId != null) e.Add(new("appid", int.Parse(appId)));
        e.Add(new("AppName", "Game")); e.Add(new("Exe", exe)); e.Add(new("StartDir", startDir));
        return e;
    }

    static VdfObject RootOf(params (string key, VdfObject e)[] entries)
    {
        var s = new VdfObject();
        foreach (var (k, e) in entries) s.Add(new(k, e));
        return new VdfObject { new("shortcuts", s) };
    }

    [Fact] public void Upsert_NewKeyIsMaxPlusOne_WhenKeysHaveGaps()
    {
        var root = RootOf(("0", Entry("1", @"C:\A")), ("2", Entry("2", @"C:\B")));
        ShortcutsFile.Upsert(root, "N", @"C:\N\n.exe", @"C:\N", "");
        var s = (VdfObject)root["shortcuts"]!;
        Assert.Equal(3, s.Count);
        Assert.Equal("3", s[2].Key);
    }

    [Fact] public void Upsert_MatchesQuotedStartDir_AndKeepsIt()
    {
        var root = RootOf(("0", Entry("-5", "\"C:\\G\\\"")));
        Assert.NotNull(ShortcutsFile.Find(root, @"C:\G"));
        var e = ShortcutsFile.Upsert(root, "Renamed", @"C:\G\x.exe", @"C:\G", "i.png");
        Assert.Single((VdfObject)root["shortcuts"]!);
        Assert.Equal(-5, e["appid"]);
        Assert.Equal("\"C:\\G\\\"", e["StartDir"]);
        Assert.Equal("Renamed", e["AppName"]);
    }

    [Fact] public void AppIdOf_WithoutAppId_ComputesFromEntryWithoutMutating()
    {
        var e = Entry(null, @"C:\G");
        Assert.Equal(ShortcutAppId.Compute("\"C:\\G\\g.exe\"", "Game"), ShortcutsFile.AppIdOf(e));
        Assert.Null(e["appid"]);
    }

    [Fact] public void Find_DoesNotMutateRoot()
    {
        var root = new VdfObject();
        Assert.Null(ShortcutsFile.Find(root, @"C:\G"));
        Assert.Empty(root);
    }
}
