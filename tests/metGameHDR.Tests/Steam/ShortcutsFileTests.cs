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

    [Fact] public void Save_WritesBackupAndAtomically()
    {
        var path = Path.GetTempFileName(); File.WriteAllBytes(path, BinaryVdfTests.Fixture());
        var root = ShortcutsFile.Load(path); ShortcutsFile.Upsert(root, "N", @"C:\N\n.exe", @"C:\N", "");
        ShortcutsFile.Save(path, root);
        Assert.Equal(BinaryVdfTests.Fixture(), File.ReadAllBytes(path + ".bak"));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal(2, ((VdfObject)ShortcutsFile.Load(path)["shortcuts"]!).Count);
    }
}
