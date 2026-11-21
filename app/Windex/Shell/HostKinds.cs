// Host-kind icons (design §6.3). Chosen per saved host; inferred for hosts without a choice.
namespace Windex.Shell;

public sealed record HostKind(string Key, string Label, GlyphSource Source, string Glyph);

public static class HostKinds
{
    public static readonly IReadOnlyList<HostKind> All =
    [
        new("pc", "This PC", GlyphSource.Fluent, "\uE977"),          // PC1
        new("desktop", "Desktop", GlyphSource.Fluent, "\uE7F4"),     // TVMonitor
        new("laptop", "Laptop", GlyphSource.Fluent, "\uE7F8"),       // DeviceLaptopNoPic
        new("server", "Server", GlyphSource.Fluent, "\uE965"),       // MediaStorageTower
        new("cloud", "Cloud VM", GlyphSource.Fluent, "\uE753"),      // Cloud
        new("web", "Web endpoint", GlyphSource.Fluent, "\uE774"),    // Globe
        new("linux", "Linux box", GlyphSource.Nerd, "\uF17C"),       // fa-linux (Font Awesome, CC BY 4.0)
        new("mini", "Mac / mini PC", GlyphSource.Nerd, "\U000F01C4"), // md-desktop_mac (Apache 2.0)
    ];

    public static HostKind Get(string? key) => All.FirstOrDefault(k => k.Key == key) ?? All.First(k => k.Key == "server");

    /// <summary>Default icon from the transport and (when known) the tailnet OS.</summary>
    public static string Infer(string? transport, string? endpoint, string? os)
    {
        if (transport == "local" || transport == "unix") return "pc";
        if (endpoint?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true) return "web";
        return os?.ToLowerInvariant() switch
        {
            "linux" => "linux",
            "macos" or "darwin" => "mini",
            "windows" => "desktop",
            _ => "server",
        };
    }
}
