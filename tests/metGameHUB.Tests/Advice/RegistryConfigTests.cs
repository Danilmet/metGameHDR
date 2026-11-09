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
}
