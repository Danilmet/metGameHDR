using System.Windows;
using MetGameHDR.Advice;

namespace MetGameHDR.Ui;

public partial class ApplyDialog : Window
{
    sealed record Row(string Key, string Current, string New);

    public ApplyDialog(string path, IReadOnlyList<EditPreview> items)
    {
        InitializeComponent();
        PathText.Text = path;
        PathText.ToolTip = path;
        ItemsList.ItemsSource = items
            .Select(i => new Row(i.Edit.Section is null ? i.Edit.Key : $"[{i.Edit.Section}] {i.Edit.Key}", i.Current ?? "нет", i.Edit.Value))
            .ToList();
    }

    void Apply_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
