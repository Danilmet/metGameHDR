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
}
