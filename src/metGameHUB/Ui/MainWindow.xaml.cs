using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MetGameHUB.Advice;
using MetGameHUB.Scan;
using MetGameHUB.Steam;
using MetGameHUB.Update;
using MetGameHUB.Watch;
using Forms = System.Windows.Forms;

namespace MetGameHUB.Ui;

public partial class MainWindow : Window
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    static readonly HttpClient ClaudeHttp = new() { Timeout = TimeSpan.FromMinutes(3) };

    readonly Settings settings;
    readonly Library lib;
    readonly Advisor advisor;
    readonly FinderRoots roots = FinderRoots.Default();
    readonly int monitorHz = Hardware.Detect().Hz is > 1 and var hz ? hz : 60;
    readonly Action onLibraryChanged;
    readonly ObservableCollection<GameRow> rows = [];
    readonly Covers covers = new(Http, AppPaths.CoversDir);
    bool scanning;
    bool addingToSteam;
    bool updating;
    GameRow? askingRow;
    readonly ChatPanel chat;
    Action? pendingCovers;
    Func<Task>? installUpdate;

    public MainWindow(Settings settings, Library lib, Action onLibraryChanged)
    {
        this.settings = settings;
        this.lib = lib;
        this.onLibraryChanged = onLibraryChanged;
        advisor = new Advisor(() => settings.ClaudeApiKey, () => settings.ClaudeBaseUrl, () => new ClaudeChoice(settings.ClaudeModel, settings.ClaudeLocate, settings.ClaudeRead, settings.ClaudeAsk), lib, AppPaths.LibraryFile, Hardware.Detect, roots, IsRunning, ClaudeHttp);
        InitializeComponent();
        chat = new ChatPanel(ChatInput, ChatChips, ChatAttach, ChatMic, ChatSend, ChatBetter, ChatWorse);
        chat.Send += (text, ready) =>
        {
            if (GamePage.DataContext is not GameRow row) return;
            ShowChatHistory(true);
            _ = SendChatAsync(row, text, ready);
        };

        GamesGrid.ItemsSource = rows;
        System.Windows.Data.CollectionViewSource.GetDefaultView(rows).Filter = r => ShowHiddenBox.IsChecked == true || !((GameRow)r).Hidden;
        RootsList.ItemsSource = settings.FolderRoots;
        KeyBox.Text = settings.SteamGridDbKey ?? "";
        ClaudeKeyBox.Text = settings.ClaudeApiKey ?? "";
        ClaudeUrlBox.Text = settings.ClaudeBaseUrl ?? "";
        ClaudeModelBox.Text = settings.ClaudeModel ?? "";
        DefaultTargetSlider.Value = (int)settings.DefaultTarget;
        DefaultTargetText.Text = GoalTargets.Label(settings.DefaultTarget, monitorHz, false);
        AutostartBox.IsChecked = Autostart.IsEnabled();
        TurnOffBox.IsChecked = settings.TurnOffHdrOnExit;
        VersionText.Text = $"Версия {Updater.Current.ToString(3)}";
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
        // An editable ComboBox has no TextChanged of its own; its inner TextBox raises the routed event (picking an item sets Text too).
        ClaudeModelBox.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, (TextChangedEventHandler)((_, _) =>
        {
            settings.ClaudeModel = ClaudeModelBox.Text.Trim() is { Length: > 0 } model ? model : null;
            SaveSettings();
        }));
        BindClaudeTask(LocateModelBox, LocateEffortBox, settings.ClaudeLocate);
        BindClaudeTask(ReadModelBox, ReadEffortBox, settings.ClaudeRead);
        BindClaudeTask(AskModelBox, AskEffortBox, settings.ClaudeAsk);
        DefaultTargetSlider.ValueChanged += (_, e) =>
        {
            settings.DefaultTarget = (GoalStep)(int)e.NewValue;
            SaveSettings();
            DefaultTargetText.Text = GoalTargets.Label(settings.DefaultTarget, monitorHz, false);
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

    // Deferred while the game page is open: filtering out its hidden game would drop the selection and close the page.
    void RefreshFilter()
    {
        if (GamePage.Visibility != Visibility.Visible) System.Windows.Data.CollectionViewSource.GetDefaultView(rows).Refresh();
    }

    void ShowHidden_Click(object sender, RoutedEventArgs e) => RefreshFilter();

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (GamesTab.IsChecked == true && GamePage.Visibility == Visibility.Visible)
        {
            if (InOpenDropDown(Keyboard.FocusedElement) || InOpenDropDown(e.OriginalSource)) return;
            if (ChatHistory.Visibility == Visibility.Visible) ShowChatHistory(false);
            else if (ConfirmLeave()) ShowGamePage(false);
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
        if (!show)
        {
            RefreshFilter();
            return;
        }
        GameStrip.SelectedItem = GamesGrid.SelectedItem;
        if (GameStrip.SelectedItem != null) GameStrip.ScrollIntoView(GameStrip.SelectedItem);
    }

    // Mouse and keyboard both land here, so switching games always goes through ConfirmLeave.
    void GameStrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GameStrip.SelectedItem is not GameRow row || ReferenceEquals(row, GamesGrid.SelectedItem)) return;
        if (!ConfirmLeave())
        {
            GameStrip.SelectedItem = GamesGrid.SelectedItem;
            return;
        }
        GamesGrid.SelectedItem = row;
        OpenGamePage(row);
    }

    // A second click on the current game's tile goes back to the grid, like "← Назад".
    void StripTile_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: GameRow row } || !ReferenceEquals(row, GamesGrid.SelectedItem)) return;
        e.Handled = true;
        if (ConfirmLeave()) ShowGamePage(false);
    }

    void OpenGamePage(GameRow row)
    {
        row.ApplyStatus = "";
        row.RefreshCanApply();
        row.RefreshConfig();
        chat.Clear();
        ShowChatHistory(false);
        ShowGamePage(true);
        PageScroll.ScrollToTop();
        Dispatcher.BeginInvoke(() => GoalSlider.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    void CloseGamePage_Click(object sender, RoutedEventArgs e)
    {
        if (ConfirmLeave()) ShowGamePage(false);
    }

    // A plain click opens the game page; Ctrl/Shift clicks keep the ListBox multi-select for "Add to Steam".
    void Card_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: GameRow row } || Keyboard.Modifiers != ModifierKeys.None) return;
        GamesGrid.SelectedItem = row;
        OpenGamePage(row);
        e.Handled = true;
    }

    void GamesGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || GamesGrid.SelectedItems.Count != 1 || GamesGrid.SelectedItem is not GameRow row) return;
        OpenGamePage(row);
        e.Handled = true;
    }

    void ChatHistoryToggle_Click(object sender, RoutedEventArgs e) => ShowChatHistory(ChatHistory.Visibility != Visibility.Visible);

    void ShowChatHistory(bool show)
    {
        ChatHistory.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ChatHistoryToggle.Content = show ? "💬 ▾" : "💬 ▴";
        if (show) _ = Dispatcher.BeginInvoke(ChatScroll.ScrollToEnd, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    void ParamsToggle_Click(object sender, RoutedEventArgs e)
    {
        var open = ParamsPanel.Visibility != Visibility.Visible;
        ParamsPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ParamsToggle.Content = open ? "Параметры ▾" : "Параметры ▸";
    }

    // ListBox captures the mouse on an item click and then selects every item the pointer enters (drag-select); dropping that capture disables it.
    void GamesGrid_GotMouseCapture(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, GamesGrid)) GamesGrid.ReleaseMouseCapture();
    }

    void GamesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
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
            var row = new GameRow(g, lib, status, onLibraryChanged, roots, () => settings.DefaultTarget, () => monitorHz, () => !string.IsNullOrWhiteSpace(settings.ClaudeApiKey));
            row.HiddenChanged += RefreshFilter;
            rows.Add(row);
        }
        if (askingRow != null && CurrentRow(askingRow) is { } replaced)
        {
            replaced.IsAsking = true;
            replaced.AskStage = askingRow.AskStage;
            replaced.AdviceStatus = askingRow.AdviceStatus;
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
                row.SetCover(image);
            else if (db != null)
                _ = FetchCoverAsync(row, db);
        }
        // Store games have no MainExe, so their icon comes from the exe the folder scanner would pick.
        var exes = rows.Select(r => (Row: r, Exe: lib.MainExeOf(r.Game))).ToList();
        _ = Task.Run(() =>
        {
            foreach (var (row, exe) in exes)
            {
                try
                {
                    if (Covers.ExeIcon(exe ?? MainExe.Pick(row.Game.InstallDir, row.Game.Name)) is { } icon)
                        Dispatcher.BeginInvoke(() => row.Icon = icon);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        });
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
        // Updating restarts the app, which would leave Steam closed in the middle of an add.
        AddToSteamButton.IsEnabled = canRescan && !updating;
        UpdateButton.IsEnabled = !addingToSteam && !updating;
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

    void NotifyDefaultsChanged()
    {
        foreach (var row in rows) row.NotifyDefaultsChanged();
    }

    static bool IsRunning(Game game) =>
        ProcessList.Pids().Select(ProcessList.Path)
            .Any(p => p != null && p.StartsWith(game.InstallDir + "\\", StringComparison.OrdinalIgnoreCase));

    CancellationTokenSource? asking;

    void Ask_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GameRow row) _ = RunAdviceAsync(row, AdviceMode.Ask);
    }

    void ResetTarget_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GameRow row) row.ResetTarget();
    }

    void Locate_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GameRow row) _ = RunAdviceAsync(row, AdviceMode.Locate);
    }

    void RefreshSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GameRow row) _ = RunAdviceAsync(row, AdviceMode.Read);
    }

    enum AdviceMode { Locate, Ask, Read }

    // Locate finds the config file (skipped for Ask when already located), then the model reads the current settings
    // (skipped for Ask on an already located file: the ask reuses a fresh snapshot or reads it itself); Ask then requests advice.
    async Task RunAdviceAsync(GameRow row, AdviceMode mode)
    {
        if (asking != null) return;
        using var cts = new CancellationTokenSource();
        asking = cts;
        askingRow = row;
        SetAsking(row, true);
        SetStatus(row, "", "");
        string? readError = null;
        try
        {
            var read = mode == AdviceMode.Read;
            if (mode == AdviceMode.Locate || (mode == AdviceMode.Ask && !row.Located))
            {
                SetStatus(row, "Ищем конфиг…", "");
                var status = await advisor.LocateAsync(row.Game, cts.Token);
                read = status.Exists && status.Format != "none";
                if (read) ShowProvisionalFromFile(row, status.Path!);
            }
            foreach (var r in Targets(row)) r.RefreshConfig();
            if (read && !row.ConfigMissing)
            {
                SetStatus(row, "Разбираем конфиг…", "");
                try
                {
                    await advisor.ReadAsync(row.Game, cts.Token);
                    RefreshRows(row);
                }
                catch (Exception ex) when (!cts.IsCancellationRequested)
                {
                    Log.Write($"Read config: {ex}");
                    readError = $"Не удалось разобрать конфиг: {ex.Message}";
                }
                if (mode != AdviceMode.Ask) SetStatus(row, "", readError ?? "");
            }
            if (mode != AdviceMode.Ask) return;
            if (row.ConfigMissing)
            {
                SetStatus(row, "", "Запустите игру один раз и повторите");
                return;
            }
            SetStatus(row, "Подбираем настройки…", "");
            await advisor.AskAsync(row.Game, row.Goal, cts.Token);
            // RebuildRows may have replaced the row during the request.
            RefreshRows(row);
            foreach (var r in Targets(row)) r.ApplyStatus = "";
            SetStatus(row, "", readError ?? "");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            SetStatus(row, "", "Запрос отменён");
        }
        catch (Exception ex)
        {
            Log.Write($"Advice: {ex}");
            var message = ex is TaskCanceledException ? "Время ожидания ответа истекло" : ex.Message;
            SetStatus(row, "", readError == null ? message : $"{readError}\n{message}");
        }
        finally
        {
            if (ReferenceEquals(asking, cts)) asking = null;
            askingRow = null;
            SetAsking(row, false);
        }
    }

    // Shares the single `asking` slot with RunAdviceAsync, so Ask and Adjust never run at once.
    async Task SendChatAsync(GameRow row, string text, IReadOnlyList<ChatAttachment> ready)
    {
        if (asking != null) return;
        using var cts = new CancellationTokenSource();
        asking = cts;
        askingRow = row;
        SetAsking(row, true);
        SetStatus(row, "Модель думает…", "");
        try
        {
            await advisor.AdjustAsync(row.Game, row.Goal, text, ready, cts.Token);
            RefreshRows(row);
            foreach (var r in Targets(row)) r.ApplyStatus = "";
            SetStatus(row, "", "");
            if (GamePage.DataContext is GameRow shown && string.Equals(shown.Game.InstallDir, row.Game.InstallDir, StringComparison.OrdinalIgnoreCase)) chat.Clear();
            _ = Dispatcher.BeginInvoke(ChatScroll.ScrollToEnd, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            SetStatus(row, "", "Запрос отменён");
        }
        catch (Exception ex)
        {
            Log.Write($"Advice chat: {ex}");
            SetStatus(row, "", ex is TaskCanceledException ? "Время ожидания ответа истекло" : ex.Message);
        }
        finally
        {
            if (ReferenceEquals(asking, cts)) asking = null;
            askingRow = null;
            SetAsking(row, false);
        }
    }

    void ClearChat_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GameRow row) return;
        if (MessageBox.Show(this, "Очистить историю уточнений?", "metGameHUB",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        advisor.ClearChat(row.Game);
        RefreshRows(row);
    }

    // Shows the raw values at once; the model's readable version replaces them when it arrives.
    void ShowProvisionalFromFile(GameRow row, string path)
    {
        try
        {
            var rows = UnrealScalability.FromFile(File.ReadAllText(path));
            foreach (var r in Targets(row)) r.ShowProvisional(rows);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    void RefreshRows(GameRow row)
    {
        foreach (var r in Targets(row))
        {
            r.LoadAdvice();
            r.RefreshCanApply();
            r.RefreshConfig();
        }
    }

    IEnumerable<GameRow> Targets(GameRow row) =>
        CurrentRow(row) is { } current && !ReferenceEquals(current, row) ? [row, current] : [row];

    void SetStatus(GameRow row, string stage, string status)
    {
        foreach (var r in Targets(row))
        {
            r.AskStage = stage;
            r.AdviceStatus = status;
        }
    }

    void SetAsking(GameRow row, bool on)
    {
        foreach (var r in Targets(row))
        {
            r.IsAsking = on;
            if (!on) r.AskStage = "";
        }
        SettingsTab.IsEnabled = !on;
        SettingsTab.ToolTip = on ? "Дождитесь ответа или отмените запрос" : null;
    }

    // True when the page may be left: no request runs, or the user agreed to cancel it.
    bool ConfirmLeave()
    {
        if (asking == null) return true;
        if (MessageBox.Show(this, "Запрос к Claude ещё выполняется и будет отменён. Выйти?", "metGameHUB",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
        asking?.Cancel();
        return true;
    }

    GameRow? CurrentRow(GameRow row) =>
        rows.FirstOrDefault(r => string.Equals(r.Game.InstallDir, row.Game.InstallDir, StringComparison.OrdinalIgnoreCase));

    void Apply_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GameRow { Latest: { } advice } row) return;
        try
        {
            var outcome = advisor.Apply(row.Game, advice);
            row.ApplyStatus = outcome switch
            {
                ApplyOutcome.Applied => "Применено",
                ApplyOutcome.GameRunning => "Закройте игру и повторите",
                ApplyOutcome.FileMissing => "Файл не найден",
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
        row.RefreshConfig();
    }

    void Restore_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GameRow row) return;
        if (MessageBox.Show(this, "Вернуть конфиг к состоянию до первого применения рекомендаций? Текущий файл сохранится как .bak.",
                "metGameHUB", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            row.ApplyStatus = advisor.Restore(row.Game) switch
            {
                ApplyOutcome.Applied => "Исходный конфиг восстановлен",
                ApplyOutcome.GameRunning => "Закройте игру и повторите",
                _ => "Нет исходной копии",
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Restore config: {ex}");
            row.ApplyStatus = ex.Message;
        }
        row.LoadAdvice();
        row.RefreshCanApply();
        row.RefreshConfig();
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

    public void ShowUpdate(Release release, Func<Task> install)
    {
        installUpdate = install;
        UpdateText.Text = $"Доступна v{release.Version.ToString(3)}";
        UpdatePanel.Visibility = Visibility.Visible;
    }

    // Set by App: checks GitHub and, when a release is found, shows the update panel.
    public Func<Task<Release?>>? CheckUpdates { get; set; }

    async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (CheckUpdates == null) return;
        CheckUpdatesButton.IsEnabled = false;
        CheckUpdatesText.Text = "Проверка…";
        try { CheckUpdatesText.Text = await CheckUpdates() is null ? "Установлена последняя версия" : ""; }
        catch (Exception ex)
        {
            Log.Write($"Update check: {ex.Message}");
            CheckUpdatesText.Text = $"Ошибка: {ex.Message}";
        }
        finally { CheckUpdatesButton.IsEnabled = true; }
    }

    async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (addingToSteam) return;
        updating = true;
        UpdateButtons();
        UpdateButton.Content = "Загрузка…";
        try { await installUpdate!(); }
        catch (Exception ex)
        {
            Log.Write($"Update: {ex}");
            UpdateText.Text = $"Ошибка: {ex.Message}";
            updating = false;
            UpdateButtons();
            UpdateButton.Content = "Обновить";
        }
    }

    static readonly string[] Efforts = ["по умолчанию", "low", "medium", "high", "xhigh", "max"];

    void BindClaudeTask(ComboBox modelBox, ComboBox effortBox, ClaudeTask task)
    {
        modelBox.ItemsSource = ClaudeModelBox.Items.Cast<ComboBoxItem>().Select(i => (string)i.Content).ToList();
        modelBox.Text = task.Model ?? "";
        modelBox.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, (TextChangedEventHandler)((_, _) =>
        {
            task.Model = modelBox.Text.Trim() is { Length: > 0 } model ? model : null;
            SaveSettings();
        }));
        effortBox.ItemsSource = Efforts;
        effortBox.SelectedIndex = Math.Max(0, Array.IndexOf(Efforts, task.Effort));
        effortBox.SelectionChanged += (_, _) =>
        {
            task.Effort = effortBox.SelectedIndex > 0 ? Efforts[effortBox.SelectedIndex] : null;
            SaveSettings();
        };
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
        if (IsVisible) MessageBox.Show(this, message, "metGameHUB", MessageBoxButton.OK, MessageBoxImage.Error);
        else MessageBox.Show(message, "metGameHUB", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
