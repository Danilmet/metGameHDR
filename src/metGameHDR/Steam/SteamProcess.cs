using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace MetGameHDR.Steam;

public static class SteamProcess
{
    public static bool IsRunning()
    {
        var procs = Process.GetProcessesByName("steam");
        foreach (var p in procs) p.Dispose();
        return procs.Length > 0;
    }

    public static async Task ShutdownAsync(string steamPath)
    {
        using (Process.Start(new ProcessStartInfo(Path.Combine(steamPath, "steam.exe"), "-shutdown") { UseShellExecute = true })) { }
        for (var i = 0; i < 60; i++)
        {
            if (!IsRunning()) return;
            await Task.Delay(500);
        }
        if (IsRunning()) throw new InvalidOperationException("Steam did not exit within 30 seconds");
    }

    public static void Start(string steamPath)
    {
        using (Process.Start(new ProcessStartInfo(Path.Combine(steamPath, "steam.exe")) { UseShellExecute = true })) { }
    }
}
