using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using MetGameHUB.Scan;
using MetGameHUB.Ui;
using MetGameHUB.Update;
using MetGameHUB.Watch;

namespace MetGameHUB;

public partial class App : Application
{
    const string ShowEventName = @"Local\metGameHUB.Show";
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
        instance = new Mutex(false, @"Local\metGameHUB", out var first);
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
            MessageBox.Show(ex.Message, "metGameHUB", MessageBoxButton.OK, MessageBoxImage.Error);
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
        Updater.Cleanup(Environment.ProcessPath!, AppPaths.UpdateDir);
        Autostart.MigrateLegacy();
        SessionEnding += (_, _) => FinishHdr();

        var settings = Settings.Load(AppPaths.SettingsFile);
        var lib = Library.Load(AppPaths.LibraryFile);
        var hdr = new DisplayConfigHdr();
        var watcher = new ProcessWatcher(lib, ProcessList.Pids, ProcessList.Path);
        var controller = this.controller = new HdrController(hdr, () => settings.TurnOffHdrOnExit, TimeSpan.FromSeconds(3));
        Theme.Apply(this);
        Theme.Watch(this);
        var window = new MainWindow(settings, lib, watcher.Reset);

        new Thread(() => { while (showEvent!.WaitOne()) Dispatcher.InvokeAsync(() => window.ShowFromTray("second instance")); }) { IsBackground = true }.Start();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        string? lastError = null;
        Tray? tray = null;
        void Stop()
        {
            timer.Stop();
            FinishHdr();
            tray!.Dispose();
        }
        tray = new Tray(window.ShowFromTray, () => { Stop(); Shutdown(); });
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

        Version? notified = null;
        async Task InstallAsync(Release r)
        {
            var exe = Environment.ProcessPath!;
            var newExe = await Updater.DownloadAsync(r, AppPaths.UpdateDir);
            Updater.Swap(exe, newExe);
            var args = window.IsVisible ? "" : "--tray";
            Stop();
            instance!.Dispose();
            try { Process.Start(exe, args); }
            catch (Exception ex)
            {
                Log.Write($"Update: new exe failed to start, restoring old: {ex.Message}");
                File.Move(exe + ".old", exe, overwrite: true);
                Process.Start(exe, args);
            }
            finally { Shutdown(); }
        }
        async Task CheckUpdatesAsync()
        {
            try
            {
                var r = await Updater.CheckAsync();
                if (r == null) return;
                if (notified != r.Version)
                {
                    notified = r.Version;
                    tray.ShowInfo($"Доступна v{r.Version.ToString(3)}");
                }
                window.ShowUpdate(r, () => InstallAsync(r));
            }
            catch (Exception ex) { Log.Write($"Update check: {ex.Message}"); }
        }
        var updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        updateTimer.Tick += (_, _) =>
        {
            // the exiting old process may still hold .old at startup
            Updater.Cleanup(Environment.ProcessPath!, AppPaths.UpdateDir);
            updateTimer.Interval = TimeSpan.FromHours(24);
            _ = CheckUpdatesAsync();
        };
        updateTimer.Start();
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
