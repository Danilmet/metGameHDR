using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MetGameHUB.Ui;

static class Backdrop
{
    // DWMWA_SYSTEMBACKDROP_TYPE = 38, DWMSBT_TRANSIENTWINDOW (Acrylic) = 3, DWMWA_USE_IMMERSIVE_DARK_MODE = 20.
    const int SystemBackdropType = 38;
    const int TransientWindow = 3;
    const int UseImmersiveDarkMode = 20;

    public static bool TryApply(Window window, bool dark)
    {
        if (HwndSource.FromHwnd(new WindowInteropHelper(window).Handle) is not { } source) return false;
        source.CompositionTarget.BackgroundColor = Colors.Transparent;
        var backdrop = TransientWindow;
        var darkMode = dark ? 1 : 0;
        var backdropOk = DwmSetWindowAttribute(source.Handle, SystemBackdropType, ref backdrop, sizeof(int)) == 0;
        return DwmSetWindowAttribute(source.Handle, UseImmersiveDarkMode, ref darkMode, sizeof(int)) == 0 && backdropOk;
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
