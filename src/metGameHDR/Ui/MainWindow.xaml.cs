using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MetGameHDR.Advice;
using MetGameHDR.Scan;
using MetGameHDR.Steam;
using MetGameHDR.Watch;
using Forms = System.Windows.Forms;

namespace MetGameHDR.Ui;

public partial class MainWindow : Window
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    static readonly HttpClient ClaudeHttp = new() { Timeout = TimeSpan.FromMinutes(3) };

    readonly Settings settings;
    readonly Library lib;
    readonly Advisor advisor;
    readonly FinderRoots roots = FinderRoots.Default();
    readonly int monitorHz = Math.Max(Hardware.Detect().Hz, 60);
    readonly Action onLibraryChanged;
    readonly ObservableCollection<GameRow> rows = [];
    readonly Covers covers = new(Http, AppPaths.CoversDir);
    bool scanning;
    bool addingToSteam;
    Action? pendingCovers;

    public MainWindow(Settings settings, Library lib, Action onLibraryChanged)
    {
        this.settings = settings;
        this.lib = lib;
        this.onLibraryChanged = onLibraryChanged;
        advisor = new Advisor(() => settings.ClaudeApiKey, () => settings.ClaudeBaseUrl, lib, AppPaths.LibraryFile, Hardware.Detect, roots, IsRunning, ClaudeHttp);
        InitializeComponent();

        GamesGrid.ItemsSource = rows;
        RootsList.ItemsSource = settings.FolderRoots;
        KeyBox.Text = settings.SteamGridDbKey ?? "";
        ClaudeKeyBox.Text = settings.ClaudeApiKey ?? "";
        ClaudeUrlBox.Text = settings.ClaudeBaseUrl ?? "";
        DefaultFpsBox.Text = settings.DefaultFps?.ToString() ?? "";
        DefaultPriorityBox.SelectedValue = settings.DefaultPriority;
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
        ClaudeKeyBox.TextChanged += (_, _) =>
        {
            settings.ClaudeApiKey = ClaudeKeyBox.Text.Trim() is { Length: > 0 } key ? key : null;
            SaveSettings();
            NotifyDefaultsChanged();
        };
        ClaudeUrlBox.TextChanged += (_, _) =>
        {
            settings.ClaudeBaseUrl = ClaudeUrlBox.Text.Trim() is { Length: > 0 } url ? url : null;
            SaveSettings();
        };
        DefaultFpsBox.TextChanged += (_, _) =>
        {
            var text = DefaultFpsBox.Text.Trim();
            if (text.Length == 0) settings.DefaultFps = null;
            else if (int.TryParse(text, out var fps) && fps > 0) settings.DefaultFps = fps;
            else return;
            SaveSettings();
            NotifyDefaultsChanged();
        };
        DefaultPriorityBox.SelectionChanged += (_, _) =>
        {
            if (DefaultPriorityBox.SelectedValue is not Priority priority) return;
            settings.DefaultPriority = priority;
            SaveSettings();
            NotifyDefaultsChanged();
        };
        SteamUserBox.SelectionChanged += (_, _) =>
        {
            settings.SteamUserId = SteamUserBox.SelectedItem as string;
            SaveSettings();
            RebuildRows();
        };
        Theme.Changed += ApplyBackdrop;
        IsVisibleChanged += (_, e) =>
        {
            Log.Write($"Window visible={e.NewValue}{Environment.NewLine}{Environment.StackTrace}");
            if ((bool)e.NewValue) RunPendingCovers();
        };

        if (lib.Games.Count > 0) RebuildRows();
        _ = RescanAsync();
    }

    public void ShowFromTray(string source)
    {
        Log.Write($"ShowFromTray: {source}");
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyBackdrop();
    }

    void ApplyBackdrop() =>
        RootGrid.SetResourceReference(BackgroundProperty, Backdrop.TryApply(this, !Theme.IsLight()) ? "WindowBackground" : "WindowBackgroundSolid");

    void CloseDetails_Click(object sender, RoutedEventArgs e) => GamesGrid.UnselectAll();

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (GamesTab.IsChecked == true && GamePage.Visibility == Visibility.Visible)
        {
            if (InOpenDropDown(Keyboard.FocusedElement) || InOpenDropDown(e.OriginalSource)) return;
            ShowGamePage(false);
            e.Handled = true;
        }
        else if (GamesGrid.SelectedItems.Count > 0)
        {
            GamesGrid.UnselectAll();
            e.Handled = true;
        }
    }

    // The popup of a ComboBox is not in its visual tree; its items reach the ComboBox through ItemsControlFromItemContainer.
    static bool InOpenDropDown(object? source)
    {
        for (var d = source as DependencyObject; d != null;
            d = d is ComboBoxItem item ? ItemsControl.ItemsControlFromItemContainer(item)
                : d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d))
            if (d is ComboBox { IsDropDownOpen: true }) return true;
        return false;
    }

    void ShowGamePage(bool show)
    {
        GamePage.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        GamesArea.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
    }

    void OpenGamePage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GameRow row) return;
        row.ApplyStatus = "";
        row.RefreshCanApply();
        ShowGamePage(true);
        Dispatcher.BeginInvoke(() => GoalFpsBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    void CloseGamePage_Click(object sender, RoutedEventArgs e) => ShowGamePage(false);

    void Card_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { IsSelected: true } && GamesGrid.SelectedItems.Count == 1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            GamesGrid.UnselectAll();
            e.Handled = true;
        }
    }

    // ListBox captures the mouse on an item click and then selects every item the pointer enters (drag-select); dropping that capture disables it.
    void GamesGrid_GotMouseCapture(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, GamesGrid)) GamesGrid.ReleaseMouseCapture();
    }

    void GamesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DetailsPanel.Visibility = GamesGrid.SelectedItems.Count == 1 ? Visibility.Visible : Visibility.Collapsed;
        if (GamesGrid.SelectedItems.Count != 1) ShowGamePage(false);
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
            rows.Add(new GameRow(g, lib, status, onLibraryChanged, roots, DefaultGoal, () => !string.IsNullOrWhiteSpace(settings.ClaudeApiKey)));
        }
        // Covers are decoded on the UI thread, so a hidden window (--tray autostart) waits until it is first shown.
        pendingCovers = () => LoadCovers(steam, shortcuts, gridDir);
        if (IsVisible) RunPendingCovers();
    }

    void RunPendingCovers()
    {
        var load = pendingCovers;
        pendingCovers = null;
        load?.Invoke();
    }

    void LoadCovers(string? steam, VdfObject? shortcuts, string? gridDir)
    {
        var db = string.IsNullOrWhiteSpace(settings.SteamGridDbKey) ? null : new SteamGridDb(Http, settings.SteamGridDbKey);
        foreach (var row in rows)
        {
            if (Covers.LocalPath(row.Game, steam, shortcuts, gridDir, AppPaths.CoversDir) is { } path
                && Covers.LoadImage(path) is { } image)
            {
                row.SetCover(image);
                continue;
            }
            row.Icon = Covers.ExeIcon(lib.MainExeOf(row.Game));
            if (db != null) _ = FetchCoverAsync(row, db);
        }
    }

    async Task FetchCoverAsync(GameRow row, SteamGridDb db)
    {
        if (await covers.FetchAsync(row.Game, lib.SearchNameOf(row.Game), db) is { } path && rows.Contains(row))
            row.SetCover(Covers.LoadImage(path));
    }

    void UpdateButtons()
    {
        var canRescan = !scanning && !addingToSteam;
        RefreshButton.IsEnabled = canRescan;
        AddAppButton.IsEnabled = canRescan;
        AddRootButton.IsEnabled = canRescan;
        RemoveRootButton.IsEnabled = canRescan;
        AddToSteamButton.IsEnabled = canRescan;
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

    GraphicsGoal DefaultGoal() => new(settings.DefaultFps ?? monitorHz, settings.DefaultPriority);

    void NotifyDefaultsChanged()
    {
        foreach (var row in rows) row.NotifyDefaultsChanged();
    }

    static bool IsRunning(Game game) =>
        ProcessList.Pids().Select(ProcessList.Path)
            .Any(p => p != null && p.StartsWith(game.InstallDir + "\\", StringComparison.OrdinalIgnoreCase));

    async void Ask_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GameRow row) return;
        row.IsAsking = true;
        row.AdviceStatus = "Запрос к Claude…";
        try
        {
            await advisor.AskAsync(row.Game, row.Goal, CancellationToken.None);
            // RebuildRows may have replaced the row during the request.
            if (CurrentRow(row) is { } current)
            {
                current.LoadAdvice();
                current.RefreshCanApply();
                current.AdviceStatus = "";
                current.ApplyStatus = "";
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Advice: {ex}");
            var message = ex is TaskCanceledException ? "Время ожидания ответа истекло" : ex.Message;
            row.AdviceStatus = message;
            if (CurrentRow(row) is { } current) current.AdviceStatus = message;
        }
        finally
        {
            row.IsAsking = false;
        }
    }

    GameRow? CurrentRow(GameRow row) =>
        rows.FirstOrDefault(r => string.Equals(r.Game.InstallDir, row.Game.InstallDir, StringComparison.OrdinalIgnoreCase));

    void DetailsPanel_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        (e.NewValue as GameRow)?.RefreshCanApply();

    void Apply_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GameRow { Latest: { Result.Config: not null } advice } row) return;
        try
        {
            row.ApplyStatus = advisor.Apply(row.Game, advice) switch
            {
                ApplyOutcome.Applied => "Применено",
                ApplyOutcome.GameRunning => "Закройте игру и повторите",
                ApplyOutcome.FileMissing => "Файл не найден",
                ApplyOutcome.PathNotCandidate => "Файл не относится к настройкам этой игры",
                ApplyOutcome.NotIni => "Файл не в формате INI",
                ApplyOutcome.BadEdit => "Некорректные правки в ответе",
                _ => "Нет файла для записи",
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Apply advice: {ex}");
            row.ApplyStatus = ex.Message;
        }
        row.LoadAdvice();
        row.RefreshCanApply();
    }

    void AddRoot_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog();
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        if (settings.FolderRoots.Contains(dialog.SelectedPath, StringComparer.OrdinalIgnoreCase)) return;
        settings.FolderRoots.Add(dialog.SelectedPath);
        RootsList.Items.Refresh();
        SaveSettings();
        _ = RescanAsync();
    }

    void RemoveRoot_Click(object sender, RoutedEventArgs e)
    {
        if (RootsList.SelectedItem is not string root) return;
        settings.FolderRoots.Remove(root);
        RootsList.Items.Refresh();
        SaveSettings();
        _ = RescanAsync();
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
