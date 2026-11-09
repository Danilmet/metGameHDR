using System.Text;
using Microsoft.Win32;
using MetGameHUB.Advice;

namespace MetGameHUB.Tests;

public sealed class TestKey : IDisposable
{
    public string Key = @"HKCU\Software\metGameHUB.Tests\" + Guid.NewGuid().ToString("N");
    public RegistryKey Reg;

    public TestKey() => Reg = Registry.CurrentUser.CreateSubKey(RegistryConfig.SubKey(Key));

    public void Dword(string name, int v) => Reg.SetValue(name, v, RegistryValueKind.DWord);
    public void FloatBits(string name, float v) => Reg.SetValue(name, BitConverter.SingleToInt32Bits(v), RegistryValueKind.DWord);
    public void Bin(string name, string v) => Reg.SetValue(name, Encoding.UTF8.GetBytes(v + "\0"), RegistryValueKind.Binary);
    public void Sz(string name, string v) => Reg.SetValue(name, v, RegistryValueKind.String);

    public void Dispose()
    {
        Reg.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(RegistryConfig.SubKey(Key), throwOnMissingSubKey: false);
    }
}

public class RegistryConfigTests
{
    [Fact] public void Normalize_LongForm_TrailingSlash() =>
        Assert.Equal(@"HKCU\Software\Co\Game", RegistryConfig.Normalize(@"hkey_current_user\Software\Co\Game\"));

    [Theory]
    [InlineData(@"HKCU\Software\Co\Game", true)]
    [InlineData(@"HKEY_CURRENT_USER\Software\Co\Game\Sub", true)]
    [InlineData(@"HKCU\Software\Co", false)]
    [InlineData(@"HKCU\Software\Microsoft\Windows", false)]
    [InlineData(@"HKCU\Software\Wow6432Node\Co\Game", false)]
    [InlineData(@"HKLM\Software\Co\Game", false)]
    [InlineData(@"C:\Games\a.ini", false)]
    public void IsAllowedKey(string key, bool expected) => Assert.Equal(expected, RegistryConfig.IsAllowedKey(key));

    [Fact] public void Exists_And_Stamp()
    {
        using var k = new TestKey();
        Assert.True(RegistryConfig.Exists(k.Key));
        Assert.False(RegistryConfig.Exists(k.Key + "x"));
        Assert.Equal(default, RegistryConfig.Stamp(k.Key + "x"));
        var before = DateTime.UtcNow.AddSeconds(-5);
        k.Dword("A_h1", 1);
        Assert.InRange(RegistryConfig.Stamp(k.Key), before, DateTime.UtcNow.AddSeconds(5));
    }

    [Fact] public void Dump_FormatsTypesSortedAndSkipsUnknown()
    {
        using var k = new TestKey();
        k.Dword("Screenmanager Resolution Width_h182942802", 1920);
        k.Dword("UnitySelectMonitor_h17969598", -1);
        k.FloatBits("Volume_h3", 0.75f);
        k.Bin("Lang_h4", "ru");
        k.Sz("Plain", "x");
        k.Reg.SetValue("Q", 5L, RegistryValueKind.QWord);
        k.Reg.SetValue("Raw", new byte[] { 0xFF, 0xFE }, RegistryValueKind.Binary);
        Assert.Equal("Lang_h4=ru\nPlain=x\nScreenmanager Resolution Width_h182942802=1920\nUnitySelectMonitor_h17969598=-1\nVolume_h3=0.75",
            RegistryConfig.Dump(k.Key));
    }

    [Fact] public void Dump_MissingKey_Empty() => Assert.Equal("", RegistryConfig.Dump(@"HKCU\Software\metGameHUB.Tests\none" + Guid.NewGuid().ToString("N")));

    [Fact] public void Apply_KeepsTypes()
    {
        using var k = new TestKey();
        k.Dword("Width_h1", 1920); k.FloatBits("Vol_h2", 0.5f); k.Bin("Lang_h3", "ru"); k.Sz("Plain", "x");
        Assert.True(RegistryConfig.Apply(k.Key, [new(null, "Width_h1", "2560"), new(null, "Vol_h2", "0.25"), new(null, "Lang_h3", "en"), new(null, "Plain", "y")]));
        Assert.Equal(2560, k.Reg.GetValue("Width_h1"));
        Assert.Equal(RegistryValueKind.DWord, k.Reg.GetValueKind("Vol_h2"));
        Assert.Equal(0.25f, BitConverter.Int32BitsToSingle((int)k.Reg.GetValue("Vol_h2")!));
        Assert.Equal("en\0"u8.ToArray(), k.Reg.GetValue("Lang_h3"));
        Assert.Equal("y", k.Reg.GetValue("Plain"));
    }

    [Fact] public void Apply_MatchesNameWithoutHash()
    {
        using var k = new TestKey();
        k.Dword("Screenmanager Fullscreen mode_h3630240806", 1);
        Assert.True(RegistryConfig.Apply(k.Key, [new(null, "screenmanager fullscreen mode", "3")]));
        Assert.Equal(3, k.Reg.GetValue("Screenmanager Fullscreen mode_h3630240806"));
    }

    [Fact] public void Apply_AmbiguousPrefix_RefusesAll()
    {
        using var k = new TestKey();
        k.Dword("Width_h1", 1); k.Dword("Width_h2", 2); k.Dword("Other_h3", 3);
        Assert.False(RegistryConfig.Apply(k.Key, [new(null, "Other_h3", "9"), new(null, "Width", "5")]));
        Assert.Equal(3, k.Reg.GetValue("Other_h3"));
    }

    [Fact] public void Apply_UnknownName_False()
    {
        using var k = new TestKey(); k.Dword("A_h1", 1);
        Assert.False(RegistryConfig.Apply(k.Key, [new(null, "B_h2", "1")]));
    }

    [Fact] public void Apply_DecimalForInt_RefusesAndWritesNothing()
    {
        using var k = new TestKey(); k.Dword("Width_h1", 1920); k.Dword("Height_h2", 1080);
        Assert.False(RegistryConfig.Apply(k.Key, [new(null, "Height_h2", "1440"), new(null, "Width_h1", "1920.0")]));
        Assert.Equal(1080, k.Reg.GetValue("Height_h2"));
    }

    [Fact] public void Apply_UnreadableBinary_False()
    {
        using var k = new TestKey(); k.Reg.SetValue("Raw_h1", new byte[] { 0xFF, 0xFE }, RegistryValueKind.Binary);
        Assert.False(RegistryConfig.Apply(k.Key, [new(null, "Raw_h1", "x")]));
    }

    [Fact] public void Apply_MissingKey_False() =>
        Assert.False(RegistryConfig.Apply(@"HKCU\Software\metGameHUB.Tests\none" + Guid.NewGuid().ToString("N"), [new(null, "A", "1")]));

    [Fact] public void Dump_And_Apply_CyrillicNames_RoundTrip()
    {
        using var k = new TestKey(); k.Bin("Качество_h1", "Высокое");
        Assert.Equal("Качество_h1=Высокое", RegistryConfig.Dump(k.Key));
        Assert.True(RegistryConfig.Apply(k.Key, [new(null, "Качество", "Низкое")]));
        Assert.Equal("Низкое", RegistryConfig.CurrentValue(k.Key, "Качество_h1"));
    }

    [Fact] public void CurrentValue_FloatAndMissing()
    {
        using var k = new TestKey(); k.FloatBits("Vol_h2", 0.75f);
        Assert.Equal("0.75", RegistryConfig.CurrentValue(k.Key, "Vol"));
        Assert.Null(RegistryConfig.CurrentValue(k.Key, "Nope"));
    }
}
