using System.Windows;
using Microsoft.Win32;

namespace MetGameHDR.Ui;

static class Theme
{
    public static event Action? Changed;

    public static bool IsLight() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", null) is not int value || value != 0;

    public static void Apply(Application app)
    {
        var dictionary = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Ui/Themes/{(IsLight() ? "Light" : "Dark")}.xaml") };
        var merged = app.Resources.MergedDictionaries;
        if (merged.Count == 0) merged.Add(dictionary);
        else merged[0] = dictionary;
        Changed?.Invoke();
    }

    public static void Watch(Application app) =>
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General) app.Dispatcher.InvokeAsync(() => Apply(app));
        };
}
