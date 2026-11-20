using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace MultiClaude;

public static class Texts
{
    private static readonly Dictionary<string, Dictionary<string, string>> Languages = Load();
    private static Dictionary<string, Dictionary<string, string>> Load()
    {
        using var stream = typeof(Texts).Assembly.GetManifestResourceStream("MultiClaude.Resources.Translations.json")
            ?? throw new InvalidDataException("Missing language resources.");
        return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(stream)!;
    }

    public static string Get(string language, string key) =>
        Languages.TryGetValue(language, out var chosen) && chosen.TryGetValue(key, out var text)
            ? text : Languages["en"].GetValueOrDefault(key, key);
}

public static class Theme
{
    private static string current = "sistema";
    private static bool subscribed;

    public static void Apply(string mode)
    {
        var app = Application.Current;
        if (mode is not ("claro" or "escuro")) mode = "sistema";
        current = mode;
        if (!subscribed)
        {
            SystemEvents.UserPreferenceChanged += OnPreferences;
            app.Exit += (_, _) => { SystemEvents.UserPreferenceChanged -= OnPreferences; subscribed = false; };
            subscribed = true;
        }
        bool dark = mode == "escuro" || mode == "sistema" && Equals(0,
            Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1));
#pragma warning disable WPF0001
        app.ThemeMode = mode switch { "escuro" => ThemeMode.Dark, "claro" => ThemeMode.Light, _ => ThemeMode.System };
#pragma warning restore WPF0001
        foreach (var (key, light, night) in new[] {
            ("Bg","#F5F4F1","#202121"), ("Panel","#FFFFFF","#2B2D2C"), ("Text","#232321","#F5F4EF"),
            ("Muted","#645F59","#B5B6B0"), ("Accent","#9A4933","#A75239"), ("Line","#D8D5CE","#494B48"),
            ("Field","#FFFFFF","#333532") })
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? night : light));
            brush.Freeze();
            app.Resources[key] = brush;
        }
    }

    private static void OnPreferences(object sender, UserPreferenceChangedEventArgs args)
    {
        if (current == "sistema" && args.Category is UserPreferenceCategory.Color or UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle)
            Application.Current?.Dispatcher.InvokeAsync(() => Apply(current));
    }
}
