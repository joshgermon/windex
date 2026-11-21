// Theme-correct resource lookup for UI built in code. Application.Current.Resources[key] answers
// for the *application* theme, but the windows may override RequestedTheme (Settings → App theme),
// so code resolves Rex brushes against the element's ActualTheme in Themes/Rex.xaml's theme
// dictionaries. Elements not yet in the tree use the main window's theme (Theme).
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace Windex.Shell;

public static class ThemeLookup
{
    private static readonly AccessibilitySettings Accessibility = new();

    public static bool HighContrast => Accessibility.HighContrast;

    /// <summary>The main window's actual theme; kept current by MainWindow.</summary>
    public static ElementTheme Theme { get; set; } = Application.Current.RequestedTheme == ApplicationTheme.Light ? ElementTheme.Light : ElementTheme.Dark;

    public static object? Get(ElementTheme theme, string key)
    {
        string name = HighContrast ? "HighContrast" : theme == ElementTheme.Light ? "Light" : "Dark";
        if (Find(Application.Current.Resources, name, key, out var value)) return value;
        return Application.Current.Resources.TryGetValue(key, out var plain) ? plain : null;
    }

    public static Brush? Brush(ElementTheme theme, string key) => Get(theme, key) as Brush;

    /// <summary>The brush for an element: its own theme once it is in the tree, else the window's.</summary>
    public static Brush? Brush(FrameworkElement scope, string key) =>
        Brush(scope.IsLoaded && scope.ActualTheme != ElementTheme.Default ? scope.ActualTheme : Theme, key);

    private static bool Find(ResourceDictionary dict, string theme, string key, out object? value)
    {
        if (dict.ThemeDictionaries.TryGetValue(theme, out var t) && t is ResourceDictionary themed && themed.TryGetValue(key, out value))
            return true;
        for (int i = dict.MergedDictionaries.Count - 1; i >= 0; i--)
            if (Find(dict.MergedDictionaries[i], theme, key, out value)) return true;
        value = null;
        return false;
    }
}
