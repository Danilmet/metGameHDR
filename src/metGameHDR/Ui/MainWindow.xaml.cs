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
    readonly Action onLibraryChanged;
    readonly ObservableCollection<GameRow> rows = [];
    bool scanning;
    bool addingToSteam;

    public MainWindow(Settings settings, Library lib, Action onLibraryChanged)
    {
        this.settings = settings;
        this.lib = lib;
        this.onLibraryChanged = onLibraryChanged;
        InitializeComponent();

        GamesGrid.ItemsSource = rows;
        RootsList.ItemsSource = settings.FolderRoots;
        KeyBox.Text = settings.SteamGridDbKey ?? "";
        AutostartBox.IsChecked = Autostart.IsEnabled();
        TurnOffBox.IsChecked = settings.TurnOffHdrOnExit;
        var steam = SteamLocator.SteamPath();
        if (steam != null)
        {
            var userdata = Path.Combine(steam, "userdata");
            if (Directory.Exists(userdata))
                SteamUserBox.ItemsSource = Directory.GetDirectories(userdata).Select(Path.GetFileName).ToList();
            SteamUserBox.SelectedItem = CurrentSteamUser(steam);
        }

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

        if (lib.Games.Count > 0) RebuildRows();
        _ = RescanAsync();
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

    string? CurrentSteamUser(string steam) =>
        !string.IsNullOrWhiteSpace(settings.SteamUserId) ? settings.SteamUserId : SteamLocator.MostRecentUserId(steam);

    void RebuildRows()
    {
        VdfObject? shortcuts = null;
        string? gridDir = null;
        var steam = SteamLocator.SteamPath();
        if (steam != null && CurrentSteamUser(steam) is { } user)
        {
            var configDir = SteamLocator.UserConfigDir(steam, user);
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
            rows.Add(new GameRow(g, lib, status, onLibraryChanged));
        }
    }

    void UpdateButtons()
    {
        RefreshButton.IsEnabled = !scanning;
        AddAppButton.IsEnabled = !scanning;
        AddToSteamButton.IsEnabled = !scanning && !addingToSteam;
    }

    async Task RescanAsync()
    {
        scanning = true;
        UpdateButtons();
        StatusText.Text = "Сканирование…";
        try
        {
            var snapshot = new Settings { FolderRoots = [.. settings.FolderRoots], Apps = [.. settings.Apps] };
            lib.Games = await Task.Run(() => LibraryScanner.Scan(LibraryScanner.DefaultSources(snapshot), Log.Write));
            onLibraryChanged();
            RebuildRows();
            lib.Save(AppPaths.LibraryFile);
        }
        catch (Exception e)
        {
            Log.Write($"Scan: {e}");
            ShowError(e.Message);
        }
        finally
        {
            StatusText.Text = "";
            scanning = false;
            UpdateButtons();
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

        try
        {
            var path = dialog.FileName;
            if (AppsSource.Resolve(path) is null)
            {
                ShowError($"Ярлык не указывает на exe: {path}");
                return;
            }

            if (!settings.Apps.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                settings.Apps.Add(path);
                settings.Save(AppPaths.SettingsFile);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Add app: {ex}");
            ShowError(ex.Message);
            return;
        }
        await RescanAsync();
    }

    async void AddToSteam_Click(object sender, RoutedEventArgs e)
    {
        var selected = GamesGrid.SelectedItems.Cast<GameRow>().Where(r => r.CanAddToSteam).ToList();
        if (selected.Count == 0) return;

        addingToSteam = true;
        UpdateButtons();
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
            addingToSteam = false;
            UpdateButtons();
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
        var on = AutostartBox.IsChecked == true;
        try { Autostart.Set(on); }
        catch (Exception ex)
        {
            Log.Write($"Autostart: {ex}");
            ShowError(ex.Message);
            AutostartBox.IsChecked = Autostart.IsEnabled();
            return;
        }
        settings.Autostart = on;
        SaveSettings();
    }

    void TurnOff_Click(object sender, RoutedEventArgs e)
    {
        settings.TurnOffHdrOnExit = TurnOffBox.IsChecked == true;
        SaveSettings();
    }

    void SaveSettings()
    {
        try { settings.Save(AppPaths.SettingsFile); }
        catch (Exception e)
        {
            Log.Write($"Save settings: {e}");
            ShowError(e.Message);
        }
    }

    void ShowError(string message)
    {
        if (IsVisible) MessageBox.Show(this, message, "metGameHDR", MessageBoxButton.OK, MessageBoxImage.Error);
        else MessageBox.Show(message, "metGameHDR", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
