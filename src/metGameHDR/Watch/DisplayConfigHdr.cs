using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MetGameHDR.Watch;

public sealed class DisplayConfigHdr : IHdrSwitch
{
    const uint QDC_ONLY_ACTIVE_PATHS = 2;
    const uint DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9;
    const uint DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE = 10;

    public bool? IsEnabled()
    {
        bool? result = null;
        foreach (var path in ActivePaths())
        {
            var info = GetColorInfo(path);
            if ((info.value & 1) == 0) continue;
            if ((info.value & 2) != 0) return true;
            result = false;
        }
        return result;
    }

    // shortcut: используется старый API type 9/10; на Windows 11 24H2+ есть DISPLAYCONFIG_SET_HDR_STATE (17), перейти если type 10 перестанет переключать.
    public void Set(bool on)
    {
        foreach (var path in ActivePaths())
        {
            if ((GetColorInfo(path).value & 1) == 0) continue;
            var state = new DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE
            {
                header = Header(path, DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE, Marshal.SizeOf<DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE>()),
                enableAdvancedColor = on ? 1u : 0u,
            };
            Check(DisplayConfigSetDeviceInfo(ref state));
        }
    }

    static DISPLAYCONFIG_PATH_INFO[] ActivePaths()
    {
        Check(GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount));
        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
        Check(QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero));
        return paths[..(int)pathCount];
    }

    static DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO GetColorInfo(DISPLAYCONFIG_PATH_INFO path)
    {
        var info = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
        {
            header = Header(path, DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO, Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>()),
        };
        Check(DisplayConfigGetDeviceInfo(ref info));
        return info;
    }

    static DISPLAYCONFIG_DEVICE_INFO_HEADER Header(DISPLAYCONFIG_PATH_INFO path, uint type, int size) => new()
    {
        type = type,
        size = (uint)size,
        adapterId = path.targetInfo.adapterId,
        id = path.targetInfo.id,
    };

    static void Check(int code)
    {
        if (code != 0) throw new Win32Exception(code);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public uint refreshRateNumerator;
        public uint refreshRateDenominator;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    struct DISPLAYCONFIG_MODE_INFO
    {
        public uint infoType;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint value;
        public uint colorEncoding;
        public uint bitsPerColorChannel;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint enableAdvancedColor;
    }

    [DllImport("user32.dll")]
    static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO requestPacket);

    [DllImport("user32.dll")]
    static extern int DisplayConfigSetDeviceInfo(ref DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE setPacket);
}
