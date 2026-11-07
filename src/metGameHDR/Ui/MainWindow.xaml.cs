using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using MetGameHDR.Scan;
using MetGameHDR.Steam;
using Forms = System.Windows.Forms;

namespace MetGameHDR.Ui;

public partial class MainWindow : Window
{
    static readonly HttpClient Http = new();

    readonly Settings settings;
    readonly Library lib;
    readonly ObservableCollection<GameRow> rows = [];

    public MainWindow(Settings settings, Library lib)
    {
        this.settings = settings;
        this.lib = lib;
        InitializeComponent();

        GamesGrid.ItemsSource = rows;
        RootsList.ItemsSource = settings.FolderRoots;
        KeyBox.Text = settings.SteamGridDbKey ?? "";
        AutostartBox.IsChecked = Autostart.IsEnabled();
        TurnOffBox.IsChecked = settings.TurnOffHdrOnExit;
        var steam = SteamLocator.SteamPath();
        var userdata = steam == null ? null : Path.Combine(steam, "userdata");
        if (userdata != null && Directory.Exists(userdata))
            SteamUserBox.ItemsSource = Directory.GetDirectories(userdata).Select(Path.GetFileName).ToList();
        SteamUserBox.SelectedItem = CurrentSteamUser(steam);

        KeyBox.TextChanged += (_, _) =>
        {
            settings.SteamGridDbKey = KeyBox.Text.Trim() is { Length: > 0 } key ? key : null;
            SaveSettings();
        };
        SteamUserBox.SelectionChanged += (_, _) =>
        {
            settings.SteamUserId = SteamUserBox.SelectedItem as string;
            SaveSettings();
            RebuildRows();
        };

        if (lib.Games.Count == 0) _ = RescanAsync();
        else RebuildRows();
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    string? CurrentSteamUser(string? steam) =>
        !string.IsNullOrWhiteSpace(settings.SteamUserId) ? settings.SteamUserId
        : steam == null ? null : SteamLocator.MostRecentUserId(steam);

    void RebuildRows()
    {
        VdfObject? shortcuts = null;
        string? gridDir = null;
        var steam = SteamLocator.SteamPath();
        if (CurrentSteamUser(steam) is { } user)
        {
            var configDir = SteamLocator.UserConfigDir(steam!, user);
            gridDir = Path.Combine(configDir, "grid");
            try { shortcuts = ShortcutsFile.Load(Path.Combine(configDir, "shortcuts.vdf")); }
            catch (Exception e) { Log.Write($"shortcuts.vdf: {e.Message}"); }
        }

        rows.Clear();
        foreach (var g in lib.Games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var status = shortcuts != null && g.Source is GameSource.Folder or GameSource.App
                ? SteamAdder.StatusOf(g, shortcuts, gridDir!)
                : (SteamStatus?)null;
            rows.Add(new GameRow(g, lib, status));
        }
    }

    async Task RescanAsync()
    {
        RefreshButton.IsEnabled = false;
        StatusText.Text = "Сканирование…";
        try
        {
            var snapshot = new Settings { FolderRoots = [.. settings.FolderRoots], Apps = [.. settings.Apps] };
            lib.Games = await Task.Run(() => LibraryScanner.Scan(LibraryScanner.DefaultSources(snapshot), Log.Write));
            lib.Save(AppPaths.LibraryFile);
            RebuildRows();
        }
        catch (Exception e)
        {
            Log.Write($"Scan: {e}");
            ShowError(e.Message);
        }
        finally
        {
            StatusText.Text = "";
            RefreshButton.IsEnabled = true;
        }
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) => await RescanAsync();

    async void AddApp_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Программы (*.exe;*.lnk)|*.exe;*.lnk",
            DereferenceLinks = false,
        };
        if (dialog.ShowDialog(this) != true) return;

        var path = dialog.FileName;
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            path = (string)shell.CreateShortcut(path).TargetPath;
        }
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        {
            ShowError($"Ярлык не указывает на exe: {path}");
            return;
        }

        if (!settings.Apps.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            settings.Apps.Add(path);
            SaveSettings();
        }
        await RescanAsync();
    }

    async void AddToSteam_Click(object sender, RoutedEventArgs e)
    {
        var selected = GamesGrid.SelectedItems.Cast<GameRow>().Where(r => r.CanAddToSteam).ToList();
        if (selected.Count == 0) return;

        AddToSteamButton.IsEnabled = false;
        try
        {
            var result = await SteamAdder.AddAsync(selected.Select(r => r.Game).ToList(), lib, settings, Http, Log.Write);
            foreach (var row in selected)
                if (result.TryGetValue(row.Game, out var status)) row.SteamStatus = status;
        }
        catch (Exception ex)
        {
            Log.Write($"Steam: {ex}");
            ShowError(ex.Message);
        }
        finally
        {
            AddToSteamButton.IsEnabled = true;
        }
    }

    void AddRoot_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog();
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        if (settings.FolderRoots.Contains(dialog.SelectedPath, StringComparer.OrdinalIgnoreCase)) return;
        settings.FolderRoots.Add(dialog.SelectedPath);
        RootsList.Items.Refresh();
        SaveSettings();
    }

    void RemoveRoot_Click(object sender, RoutedEventArgs e)
    {
        if (RootsList.SelectedItem is not string root) return;
        settings.FolderRoots.Remove(root);
        RootsList.Items.Refresh();
        SaveSettings();
    }

    void Autostart_Click(object sender, RoutedEventArgs e)
    {
        settings.Autostart = AutostartBox.IsChecked == true;
        Autostart.Set(settings.Autostart);
        SaveSettings();
    }

    void TurnOff_Click(object sender, RoutedEventArgs e)
    {
        settings.TurnOffHdrOnExit = TurnOffBox.IsChecked == true;
        SaveSettings();
    }

    void SaveSettings() => settings.Save(AppPaths.SettingsFile);

    void ShowError(string message) =>
        MessageBox.Show(this, message, "metGameHDR", MessageBoxButton.OK, MessageBoxImage.Error);
}
