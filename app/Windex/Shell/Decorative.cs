// Icon glyphs drawn as text are private-use characters: a screen reader would announce noise.
// The row, tab or button around them carries the name, so the glyph itself leaves the UIA tree.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;

namespace Windex.Shell;

public static class DecorativeExtensions
{
    /// <summary>Hide a decorative glyph from UI Automation (raw view only).</summary>
    public static T Decorative<T>(this T element) where T : UIElement
    {
        AutomationProperties.SetAccessibilityView(element, AccessibilityView.Raw);
        return element;
    }
}
