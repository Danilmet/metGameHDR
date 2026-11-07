using System.Windows;
using MetGameHDR.Advice;

namespace MetGameHDR.Ui;

public partial class ApplyDialog : Window
{
    sealed record Row(string Key, string Current, string New);

    public ApplyDialog(IReadOnlyList<EditPreview> items)
    {
        InitializeComponent();
        ItemsList.ItemsSource = items
            .Select(i => new Row(i.Edit.Section is null ? i.Edit.Key : $"[{i.Edit.Section}] {i.Edit.Key}", i.Current ?? "—", i.Edit.Value))
            .ToList();
    }

    void Apply_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
