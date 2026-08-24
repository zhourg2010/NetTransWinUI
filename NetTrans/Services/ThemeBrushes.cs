using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace NetTrans.Services;

/// <summary>
/// Resolves a token brush for the theme currently in effect.
///
/// The design tokens live in Tokens.xaml's ThemeDictionaries, which XAML picks
/// between automatically -- but view models that hand a Brush back to a binding
/// have to choose one themselves. Indexing Application.Current.Resources would
/// silently pin whichever theme happened to be active when the dictionary was
/// first walked, so the theme dictionary is looked up explicitly here and the
/// small cache is dropped whenever the app theme changes.
/// </summary>
public static class ThemeBrushes
{
    private static readonly Dictionary<string, Brush> Cache = new();
    private static ApplicationTheme _theme = ApplicationTheme.Light;

    /// <summary>Called by the shell when the effective theme changes, so cached brushes are re-resolved.</summary>
    public static void SetTheme(ApplicationTheme theme)
    {
        if (_theme == theme) return;
        _theme = theme;
        Cache.Clear();
    }

    public static Brush Get(string key)
    {
        if (Cache.TryGetValue(key, out var cached)) return cached;

        string themeKey = _theme == ApplicationTheme.Dark ? "Dark" : "Light";
        var brush = Find(Application.Current.Resources, key, themeKey) as Brush
            ?? (Brush)Application.Current.Resources[key];

        Cache[key] = brush;
        return brush;
    }

    private static object? Find(ResourceDictionary dictionary, string key, string themeKey)
    {
        if (dictionary.ThemeDictionaries.TryGetValue(themeKey, out var themed)
            && themed is ResourceDictionary themeDictionary
            && themeDictionary.TryGetValue(key, out var value))
        {
            return value;
        }

        foreach (var merged in dictionary.MergedDictionaries)
        {
            if (Find(merged, key, themeKey) is { } found) return found;
        }

        return null;
    }
}
