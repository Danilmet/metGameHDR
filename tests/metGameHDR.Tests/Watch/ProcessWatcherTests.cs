using MetGameHDR.Scan;
using MetGameHDR.Watch;

namespace MetGameHDR.Tests.Watch;

public class ProcessWatcherTests
{
    static Library Lib() => new() { Games = [new("G", @"C:\G", GameSource.Folder, null)] };

    [Fact]
    public void Tick_DetectsGameAndForgetsItOnExit()
    {
        int[] pids = [1, 2];
        var w = new ProcessWatcher(Lib(), () => pids, p => p == 2 ? @"C:\G\g.exe" : @"C:\W\explorer.exe");
        Assert.True(w.Tick());
        Assert.Equal([2], w.GamePids);
        pids = [1];
        Assert.False(w.Tick());
        Assert.Empty(w.GamePids);
    }

    [Fact]
    public void Tick_QueriesPathOnlyForNewPids()
    {
        var asked = new List<int>();
        int[] pids = [1];
        var w = new ProcessWatcher(Lib(), () => pids, p => { asked.Add(p); return null; });
        w.Tick(); w.Tick();
        pids = [1, 3];
        w.Tick();
        Assert.Equal([1, 3], asked);
    }

    [Fact]
    public void Reset_ReevaluatesRunningPidsAgainstCurrentLibrary()
    {
        var lib = new Library();
        var w = new ProcessWatcher(lib, () => [2], _ => @"C:\G\g.exe");
        Assert.False(w.Tick());
        lib.Games = Lib().Games;
        Assert.False(w.Tick());
        w.Reset();
        Assert.True(w.Tick());
        lib.For(lib.Games[0]).HdrEnabled = false;
        w.Reset();
        Assert.False(w.Tick());
    }

    [Fact]
    public void Tick_DropsGamePidReusedByAnotherProcess()
    {
        var exe = @"C:\G\g.exe";
        var w = new ProcessWatcher(Lib(), () => [5], _ => exe);
        Assert.True(w.Tick());
        exe = @"C:\W\notepad.exe";
        Assert.False(w.Tick());
        Assert.Empty(w.GamePids);
    }

    [Fact]
    public void Tick_IgnoresPidsWithoutPath()
    {
        var w = new ProcessWatcher(Lib(), () => [7], _ => null);
        Assert.False(w.Tick());
    }

    [Fact]
    public void ProcessList_SeesOwnProcess()
    {
        Assert.Contains(Environment.ProcessId, ProcessList.Pids());
        Assert.NotNull(ProcessList.Path(Environment.ProcessId));
    }

    [Fact]
    public void ProcessList_PathKeepsNonAsciiChars()
    {
        var dir = Path.Combine(Path.GetTempPath(), "игра游戏" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "cmd.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), exe);
        System.Diagnostics.Process? proc = null;
        try
        {
            proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "/c ping -n 20 127.0.0.1 >nul") { CreateNoWindow = true, UseShellExecute = false });
            Assert.Equal(exe, ProcessList.Path(proc!.Id), ignoreCase: true);
        }
        finally
        {
            proc?.Kill();
            proc?.WaitForExit();
            Directory.Delete(dir, true);
        }
    }
}
