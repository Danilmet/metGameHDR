using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MetGameHUB.Advice;
using Microsoft.Win32;

namespace MetGameHUB.Ui;

public sealed record PendingAttachment(string Name, ChatAttachment? Ready, string? Error)
{
    public string Label => Error == null ? Name : $"{Name} — {Error}";
    public bool Refused => Error != null;
}

// Input side of the advice chat: draft text, attachments (picker, drop, Ctrl+V), voice input and quick feedback.
public sealed class ChatPanel
{
    const string Filter = "Поддерживаемые файлы|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.pdf;*.txt;*.log;*.ini;*.cfg;*.json;*.csv;*.xml|Все файлы|*.*";

    readonly TextBox input;
    readonly Button send;
    readonly ObservableCollection<PendingAttachment> pending = [];

    public event Action<string, IReadOnlyList<ChatAttachment>>? Send;

    public ChatPanel(TextBox input, ItemsControl chips, Button attach, Button mic, Button send, Button better, Button worse)
    {
        this.input = input;
        this.send = send;
        chips.ItemsSource = pending;
        pending.CollectionChanged += (_, _) => UpdateSend();
        input.TextChanged += (_, _) => UpdateSend();
        input.PreviewKeyDown += Input_PreviewKeyDown;
        input.PreviewDragOver += (_, e) => AcceptFiles(e);
        input.PreviewDragEnter += (_, e) => AcceptFiles(e);
        input.PreviewDrop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
            foreach (var path in paths) AddFile(path);
            e.Handled = true;
        };
        chips.AddHandler(Button.ClickEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is FrameworkElement { DataContext: PendingAttachment a }) pending.Remove(a);
        }));
        attach.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Multiselect = true, Filter = Filter };
            if (dialog.ShowDialog() == true)
                foreach (var path in dialog.FileNames) AddFile(path);
        };
        mic.Click += (_, _) =>
        {
            input.Focus();
            WinH();
        };
        send.Click += (_, _) => Submit();
        better.Click += (_, _) => Draft("После применения стало лучше: ");
        worse.Click += (_, _) => Draft("После применения стало хуже: ");
        UpdateSend();
    }

    public void Clear()
    {
        input.Clear();
        pending.Clear();
    }

    void UpdateSend() => send.IsEnabled = input.Text.Trim().Length > 0 || pending.Any(a => a.Ready != null);

    void Submit()
    {
        if (!send.IsEnabled) return;
        Send?.Invoke(input.Text.Trim(), pending.Where(a => a.Ready != null).Select(a => a.Ready!).ToList());
    }

    void Draft(string text)
    {
        input.Text = text;
        input.Focus();
        input.CaretIndex = input.Text.Length;
    }

    void AddFile(string path)
    {
        var name = Path.GetFileName(path);
        try { pending.Add(new(name, ChatAttachments.Prepare(path), null)); }
        catch (Exception e) when (e is AdviceException or IOException or UnauthorizedAccessException)
        {
            pending.Add(new(name, null, e.Message));
        }
    }

    static void AcceptFiles(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.IsKeyDown(Key.LeftShift) && !Keyboard.IsKeyDown(Key.RightShift))
        {
            Submit();
            e.Handled = true;
        }
        else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && ClipboardImage() is { } image)
        {
            var name = $"скриншот-{DateTime.Now:HHmmss}.png";
            try { pending.Add(new(name, ChatAttachments.FromImage(image, name), null)); }
            catch (Exception ex) when (ex is AdviceException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException) { pending.Add(new(name, null, ex.Message)); }
            e.Handled = true;
        }
    }

    // The clipboard may be locked by another process; fall back to the ordinary paste then.
    static System.Windows.Media.Imaging.BitmapSource? ClipboardImage()
    {
        try { return Clipboard.ContainsImage() ? Clipboard.GetImage() : null; }
        catch (COMException) { return null; }
    }

    const byte VkLWin = 0x5B, VkH = 0x48;
    const uint KeyUp = 2;

    [DllImport("user32.dll")]
    static extern void keybd_event(byte vk, byte scan, uint flags, nuint extra);

    // Windows voice typing; where it is unavailable nothing happens.
    static void WinH()
    {
        keybd_event(VkLWin, 0, 0, 0);
        keybd_event(VkH, 0, 0, 0);
        keybd_event(VkH, 0, KeyUp, 0);
        keybd_event(VkLWin, 0, KeyUp, 0);
    }
}
