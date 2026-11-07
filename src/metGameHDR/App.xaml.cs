using System.Configuration;
using System.Data;
using System.Runtime.InteropServices;
using System.Windows;
using MetGameHDR;
using MetGameHDR.Watch;

namespace metGameHDR;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args is ["--hdr", var command])
        {
            RunHdrCommand(command);
            Shutdown(0);
            return;
        }
        new MainWindow().Show();
    }

    static void RunHdrCommand(string command)
    {
        var hdr = new DisplayConfigHdr();
        if (command is "on" or "off") hdr.Set(command == "on");
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
