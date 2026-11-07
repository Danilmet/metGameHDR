using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using MetGameHDR.Scan;
using MetGameHDR.Ui;
using MetGameHDR.Watch;

namespace MetGameHDR;

public partial class App : Application
{
    const string ShowEventName = @"Local\metGameHDR.Show";
    static Mutex? instance;
    static EventWaitHandle? showEvent;
    HdrController? controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args is ["--hdr", var command])
        {
            RunHdrCommand(command);
            Shutdown(0);
            return;
        }
        showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        instance = new Mutex(false, @"Local\metGameHDR", out var first);
        if (!first)
        {
            showEvent.Set();
            Shutdown(0);
            return;
        }
        try { StartTray(showWindow: !e.Args.Contains("--tray")); }
        catch (Exception ex)
        {
            Log.Write($"Startup: {ex}");
            MessageBox.Show(ex.Message, "metGameHDR", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        FinishHdr();
        base.OnExit(e);
    }

    void FinishHdr()
    {
        try { controller?.Finish(); }
        catch (Exception ex) { Log.Write($"Exit: {ex}"); }
    }

    void StartTray(bool showWindow)
    {
        SessionEnding += (_, _) => FinishHdr();

        var settings = Settings.Load(AppPaths.SettingsFile);
        var lib = Library.Load(AppPaths.LibraryFile);
        var hdr = new DisplayConfigHdr();
        var watcher = new ProcessWatcher(lib, ProcessList.Pids, ProcessList.Path);
        var controller = this.controller = new HdrController(hdr, () => settings.TurnOffHdrOnExit, TimeSpan.FromSeconds(3));
        var window = new MainWindow(settings, lib, watcher.Reset);

        new Thread(() => { while (showEvent!.WaitOne()) Dispatcher.InvokeAsync(() => window.ShowFromTray("second instance")); }) { IsBackground = true }.Start();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        string? lastError = null;
        Tray? tray = null;
        tray = new Tray(window.ShowFromTray, () =>
        {
            timer.Stop();
            FinishHdr();
            tray!.Dispose();
            Shutdown();
        });
        timer.Tick += (_, _) =>
        {
            try
            {
                if (!tray.Paused) controller.Update(watcher.Tick(), DateTime.UtcNow);
                tray.SetHdr(hdr.IsEnabled() == true);
                lastError = null;
            }
            catch (Exception ex)
            {
                Log.Write($"Tick: {ex}");
                if (ex.Message != lastError) tray.ShowError(ex.Message);
                lastError = ex.Message;
            }
        };
        timer.Start();
        if (showWindow) window.Show();
        DispatcherUnhandledException += (_, a) =>
        {
            Log.Write($"Unhandled: {a.Exception}");
            a.Handled = true;
        };
    }

    static void RunHdrCommand(string command)
    {
        var hdr = new DisplayConfigHdr();
        if (command is "on" or "off")
        {
            Log.Write($"--hdr {command}: info2 before {hdr.ColorInfo2Bits()}");
            hdr.Set(command == "on");
            Log.Write($"--hdr {command}: info2 after {hdr.ColorInfo2Bits()}");
        }
        var line = command is "status" or "on" or "off"
            ? hdr.IsEnabled()?.ToString() ?? "null"
            : $"Unknown --hdr command: {command}";
        AttachConsole(-1);
        Console.WriteLine(line);
        Log.Write($"--hdr {command}: {line}");
    }

    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int processId);
}
