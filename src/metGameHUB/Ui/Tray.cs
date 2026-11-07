using System.Runtime.InteropServices;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace MetGameHUB.Ui;

public sealed class Tray : IDisposable
{
    readonly Forms.NotifyIcon notifyIcon;
    readonly Forms.ToolStripMenuItem pause = new("Пауза слежения") { CheckOnClick = true };
    readonly Drawing.Icon offIcon;
    readonly Drawing.Icon onIcon;
    bool? hdr;

    public Tray(Action<string> open, Action exit)
    {
        offIcon = Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!)!;
        onIcon = WithGreenDot(offIcon);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => open("tray menu"));
        menu.Items.Add(pause);
        menu.Items.Add("Выход", null, (_, _) => exit());
        notifyIcon = new Forms.NotifyIcon { ContextMenuStrip = menu };
        notifyIcon.DoubleClick += (_, _) => open("tray double-click");
        SetHdr(false);
        notifyIcon.Visible = true;
    }

    public bool Paused => pause.Checked;

    public void SetHdr(bool on)
    {
        if (hdr == on) return;
        hdr = on;
        notifyIcon.Icon = on ? onIcon : offIcon;
        notifyIcon.Text = on ? "HDR: вкл" : "HDR: выкл";
    }

    public void ShowError(string message) =>
        notifyIcon.ShowBalloonTip(3000, "metGameHUB", message, Forms.ToolTipIcon.Error);

    public void ShowInfo(string message) =>
        notifyIcon.ShowBalloonTip(3000, "metGameHUB", message, Forms.ToolTipIcon.Info);

    static Drawing.Icon WithGreenDot(Drawing.Icon icon)
    {
        using var bitmap = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.DrawIcon(icon, new Drawing.Rectangle(0, 0, 32, 32));
            g.FillEllipse(Drawing.Brushes.LimeGreen, 19, 19, 12, 12);
        }
        return Drawing.Icon.FromHandle(bitmap.GetHicon());
    }

    public void Dispose()
    {
        notifyIcon.Visible = false;
        notifyIcon.Dispose();
        // Icon.FromHandle does not own the HICON from GetHicon, so it must be freed explicitly.
        DestroyIcon(onIcon.Handle);
        onIcon.Dispose();
        offIcon.Dispose();
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);
}
