using System.Runtime.InteropServices;
using System.Text;

namespace MetGameHDR.Watch;

public static class ProcessList
{
    const uint QueryLimited = 0x1000;

    [DllImport("psapi.dll", SetLastError = true)]
    static extern bool EnumProcesses([Out] uint[] pids, uint cb, out uint bytesReturned);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    public static int[] Pids()
    {
        var buf = new uint[1024];
        while (true)
        {
            if (!EnumProcesses(buf, (uint)(buf.Length * sizeof(uint)), out var bytes)) return [];
            var n = (int)(bytes / sizeof(uint));
            if (n < buf.Length)
            {
                var res = new int[n];
                for (var i = 0; i < n; i++) res[i] = (int)buf[i];
                return res;
            }
            buf = new uint[buf.Length * 2];
        }
    }

    public static string? Path(int pid)
    {
        var h = OpenProcess(QueryLimited, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }
}
