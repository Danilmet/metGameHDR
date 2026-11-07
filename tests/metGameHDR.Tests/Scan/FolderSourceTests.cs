using System.Diagnostics;
using MetGameHDR.Scan;

namespace metGameHDR.Tests.Scan;

public class FolderSourceTests : TempFsTest
{
    private string? _denied;

    [Fact]
    public void Scan_KeepsFoldersWithoutRootExe()
    {
        var root = TempDir(); var saves = Path.Combine(root, "Saves"); Directory.CreateDirectory(saves); var g = Path.Combine(root, "Control"); Touch(g, "Control.exe", 1);
        var games = new FolderSource([root, @"C:\nope\" + Guid.NewGuid()]).Scan().OrderBy(x => x.Name).ToList();
        Assert.Equal(2, games.Count);
        Assert.Equal("Control", games[0].Name); Assert.Equal(Path.Combine(g, "Control.exe"), games[0].MainExe);
        Assert.Equal("Saves", games[1].Name); Assert.Equal(saves, games[1].InstallDir); Assert.Null(games[1].MainExe);
    }

    [Fact]
    public void Scan_UnreadableFolderDoesNotAbortSiblings()
    {
        var root = TempDir();
        Touch(Path.Combine(root, "A"), "A.exe", 1);
        _denied = Path.Combine(root, "Locked"); Touch(_denied, "Locked.exe", 1);
        Touch(Path.Combine(root, "Z"), "Z.exe", 1);
        Icacls($"\"{_denied}\" /deny \"{Environment.UserName}\":(OI)(CI)(RD,RX)");

        var games = new FolderSource([root]).Scan().Select(g => g.Name).OrderBy(n => n).ToList();
        Assert.Equal(["A", "Z"], games);
    }

    public override void Dispose()
    {
        if (_denied is not null) Icacls($"\"{_denied}\" /remove:d \"{Environment.UserName}\"");
        base.Dispose();
    }

    private static void Icacls(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("icacls", args) { CreateNoWindow = true, RedirectStandardOutput = true, UseShellExecute = false })!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }
}
