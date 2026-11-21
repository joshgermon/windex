// Window material (design §4): system backdrop, ground and card brushes per material, with the
// Solid fallback whenever transparency effects are off, Energy saver is on, or high contrast is.
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windex.Shell;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Windex.Services;

public static class WindowMaterial
{
    private static readonly UISettings UiSettings = new();

    /// <summary>Raised (on any thread) when a system setting that affects materials changes.</summary>
    public static event EventHandler? SystemChanged;

    static WindowMaterial()
    {
        UiSettings.AdvancedEffectsEnabledChanged += (_, _) => SystemChanged?.Invoke(null, EventArgs.Empty);
        Microsoft.Windows.System.Power.PowerManager.EnergySaverStatusChanged += (_, _) => SystemChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Chrome motion is on (see <see cref="Motion"/> for its durations).</summary>
    public static bool AnimationsEnabled(AppSettings settings) => settings.Animations != "off" && UiSettings.AnimationsEnabled;

    /// <summary>The material actually in effect, after system fallbacks.</summary>
    public static string Effective(AppSettings settings)
    {
        if (ThemeLookup.HighContrast || !UiSettings.AdvancedEffectsEnabled) return "solid";
        if (Microsoft.Windows.System.Power.PowerManager.EnergySaverStatus == Microsoft.Windows.System.Power.EnergySaverStatus.On) return "solid";
        return settings.Material;
    }

    /// <summary>Apply the material to a window and its root element (call again on theme changes).</summary>
    public static void Apply(Window window, AppSettings settings)
    {
        string material = Effective(settings);
        bool keepActive = settings.KeepGlassWhenInactive;
        switch (material)
        {
            case "mica":
                if (window.SystemBackdrop is not MicaBackdrop { Kind: MicaKind.Base }) window.SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
                break;
            case "acrylic":
                if (window.SystemBackdrop is RexAcrylicBackdrop acrylic) acrylic.Configure(settings.GlassOpacity, keepActive);
                else window.SystemBackdrop = new RexAcrylicBackdrop(window, settings.GlassOpacity, keepActive);
                break;
            case "solid":
                window.SystemBackdrop = null;
                break;
            default:
                if (window.SystemBackdrop is not MicaBackdrop { Kind: MicaKind.BaseAlt }) window.SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
                break;
        }
        ApplyBrushes(material, settings.GlassOpacity);
    }

    /// <summary>
    /// Swap the material-dependent brush colours in Themes/Rex.xaml (both themes). As on Rex Mac,
    /// cards are a lighter glass than the ground they float on: the focused card nearly opaque so
    /// its text stays crisp, the others thinner so the ground's tint reads through them.
    /// </summary>
    private static void ApplyBrushes(string material, double glassOpacity)
    {
        // Glass opacity thins the inactive cards fully (0.55×–1×) but the focused one only a little
        // (0.8×–1×), so the pane being read stays crisp over a busy desktop.
        double t = (Math.Clamp(glassOpacity, 40, 100) - 40) / 60;
        double factor = 0.55 + t * 0.45, focusFactor = 0.8 + t * 0.2;
        foreach (var (theme, dark) in new[] { ("Light", false), ("Dark", true) })
        {
            var (focused, inactive, ground) = material switch
            {
                "acrylic" => dark
                    ? (Scale("#DB2B2A31", focusFactor), Scale("#8C2B2A31", factor), "#00000000")
                    : (Scale("#F2FFFFFF", focusFactor), Scale("#8CFFFFFF", factor), "#00000000"),
                "solid" => dark ? ("#FF2B2A31", "#FF26252B", "#FF1C1C1E") : ("#FFFFFFFF", "#FFF9F9FA", "#FFF0EFF2"),
                _ => dark ? ("#EB2B2A31", "#AD2B2A31", "#00000000") : ("#F2FFFFFF", "#ADFFFFFF", "#00000000"),
            };
            Set(theme, "RexPaneCardBrush", focused);
            Set(theme, "RexPaneCardInactiveBrush", inactive);
            Set(theme, "RexGroundBrush", ground);
        }

        static string Scale(string hex, double factor)
        {
            var c = ColorMath.Hex(hex);
            return $"#{(byte)Math.Round(c.A * factor):X2}{c.R:X2}{c.G:X2}{c.B:X2}";
        }
    }

    private static void Set(string theme, string key, string hex)
    {
        foreach (var dict in Application.Current.Resources.MergedDictionaries)
            if (dict.ThemeDictionaries.TryGetValue(theme, out var t) && t is ResourceDictionary themed && themed.TryGetValue(key, out var v) && v is SolidColorBrush brush)
                brush.Color = ColorMath.Hex(hex);
    }
}

/// <summary>
/// Desktop Acrylic ("Frosted glass", design §4.4): blurs the windows and desktop behind the window.
/// Customised controllers stop following the theme, so the tint is re-applied on theme changes.
/// </summary>
public sealed partial class RexAcrylicBackdrop : SystemBackdrop
{
    private readonly Window _window;
    private DesktopAcrylicController? _controller;
    private SystemBackdropConfiguration? _config;
    private FrameworkElement? _root;
    private double _glass;
    private bool _keepActive;
    private bool _windowActive = true;

    public RexAcrylicBackdrop(Window window, double glassOpacity, bool keepActive)
    {
        _window = window;
        _glass = glassOpacity;
        _keepActive = keepActive;
    }

    public void Configure(double glassOpacity, bool keepActive)
    {
        _glass = glassOpacity;
        _keepActive = keepActive;
        Tint();
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(target, xamlRoot);
        _root = xamlRoot.Content as FrameworkElement;
        if (_root is not null) _root.ActualThemeChanged += OnThemeChanged;
        _window.Activated += OnActivated;
        _config = new SystemBackdropConfiguration { IsInputActive = true };
        _controller = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Thin };
        Tint();
        _controller.SetSystemBackdropConfiguration(_config);
        _controller.AddSystemBackdropTarget(target);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        base.OnTargetDisconnected(target);
        if (_root is not null) _root.ActualThemeChanged -= OnThemeChanged;
        _window.Activated -= OnActivated;
        _controller?.RemoveSystemBackdropTarget(target);
        _controller?.Dispose();
        _controller = null;
    }

    private void OnThemeChanged(FrameworkElement sender, object args) => Tint();

    /// <summary>Follow window activation (fallback colour when inactive) unless pinned active.</summary>
    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        _windowActive = e.WindowActivationState != WindowActivationState.Deactivated;
        if (_config is not null) _config.IsInputActive = _keepActive || _windowActive;
    }

    private void Tint()
    {
        if (_controller is null || _config is null) return;
        bool dark = _root?.ActualTheme != ElementTheme.Light;
        _config.Theme = dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
        // Glass opacity 40–100 % → how much of the blurred desktop reads through (screenshot 3's
        // frosted window at the 70 % default); the cards on top stay denser so text keeps its contrast.
        double t = (Math.Clamp(_glass, 40, 100) - 40) / 60;
        double luminosity = dark ? 0.12 + t * 0.70 : 0.30 + t * 0.62;
        _controller.TintColor = dark ? Color.FromArgb(255, 0x1C, 0x1B, 0x21) : Color.FromArgb(255, 0xF4, 0xF2, 0xF7);
        _controller.TintOpacity = dark ? 0.20f : 0.12f;
        _controller.LuminosityOpacity = (float)luminosity;
        _controller.FallbackColor = dark ? Color.FromArgb(255, 0x20, 0x20, 0x20) : Color.FromArgb(255, 0xF3, 0xF3, 0xF3);
        _config.IsInputActive = _keepActive || _windowActive;
    }
}
