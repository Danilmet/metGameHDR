using MetGameHDR.Scan;

namespace metGameHDR.Tests.Scan;

public class AppsSourceTests : TempFsTest
{
    [Fact]
    public void Scan_PlainExeTakesNameFromFileAndSkipsMissing()
    {
        var dir = TempDir(); Touch(dir, "Tool.exe", 0);
        var exe = Path.Combine(dir, "Tool.exe");
        var g = Assert.Single(new AppsSource([exe, Path.Combine(dir, "gone.exe")]).Scan());
        Assert.Equal(new Game("Tool", dir, GameSource.App, exe), g);
    }

    [Fact]
    public void Scan_LnkKeepsTargetAndArguments()
    {
        var dir = TempDir(); var appDir = Path.Combine(dir, "app"); Touch(appDir, "Update.exe", 0);
        var target = Path.Combine(appDir, "Update.exe");
        var lnk = Path.Combine(dir, "Discord.lnk");
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        var sc = shell.CreateShortcut(lnk);
        sc.TargetPath = target;
        sc.Arguments = "--processStart Discord.exe";
        sc.Save();

        var g = Assert.Single(new AppsSource([lnk]).Scan());
        Assert.Equal(new Game("Discord", appDir, GameSource.App, target, "--processStart Discord.exe"), g);
    }
}
