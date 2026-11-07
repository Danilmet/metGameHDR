using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MetGameHDR.Advice;

public sealed record HardwareInfo(string Gpu, int? VramGb, string Cpu, int RamGb, int Width, int Height, int Hz, string Os);

public static class Hardware
{
    const string Unknown = "неизвестно";
    const int ENUM_CURRENT_SETTINGS = -1;
    const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE mode);

    public static HardwareInfo Detect()
    {
        var (gpu, vram) = Gpu();
        var (w, h, hz) = Monitor();
        return new HardwareInfo(gpu, vram, Cpu(), Ram(), w, h, hz, Os());
    }

    static (string, int?) Gpu()
    {
        string? name = null;
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(DisplayClass);
            if (cls == null) return (Unknown, null);
            foreach (var sub in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)).Order())
            {
                using var k = cls.OpenSubKey(sub);
                var desc = k?.GetValue("DriverDesc") as string;
                if (string.IsNullOrWhiteSpace(desc)) continue;
                name ??= desc;
                var bytes = k!.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long l => l,
                    byte[] { Length: >= 8 } b => BitConverter.ToInt64(b, 0),
                    _ => 0L,
                };
                if (bytes > 0) return (desc, (int)Math.Round(bytes / 1073741824.0));
            }
        }
        catch { }
        return (name ?? Unknown, null);
    }

    static string Cpu()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var s = (k?.GetValue("ProcessorNameString") as string)?.Trim();
            return string.IsNullOrEmpty(s) ? Unknown : s;
        }
        catch { return Unknown; }
    }

    static int Ram()
    {
        try { return (int)Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0); }
        catch { return 0; }
    }

    static (int, int, int) Monitor()
    {
        try
        {
            var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
            if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm))
                return ((int)dm.dmPelsWidth, (int)dm.dmPelsHeight, (int)dm.dmDisplayFrequency);
        }
        catch { }
        return (0, 0, 0);
    }

    static string Os()
    {
        try { return Environment.OSVersion.VersionString; }
        catch { return Unknown; }
    }
}
