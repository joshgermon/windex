// Windows toasts for program notifications (design §9.10) through AppNotificationManager
// (unpackaged registration), with the program's tile as the logo and the taskbar flash when the
// window is inactive.
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Windex.Core;
using Windex.Shell;
using Windows.UI;

namespace Windex.Services;

/// <summary>Where a clicked toast should land.</summary>
public sealed record NotificationTarget(string Host, string Session, string? Window, ulong Pane);

public sealed class Notifications
{
    private readonly string _cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Windex", "TileCache");

    /// <summary>Raised on a thread-pool thread when the user clicks a toast or its Open button.</summary>
    public event EventHandler<NotificationTarget>? Invoked;

    public bool Available { get; }
    public string? Problem { get; }

    public Notifications()
    {
        try
        {
            // Subscribe first, then register (the documented order).
            AppNotificationManager.Default.NotificationInvoked += OnInvoked;
            var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "Windex-256.png");
            AppNotificationManager.Default.Register("Windex", new Uri(icon));
            Available = AppNotificationManager.IsSupported();
        }
        catch (Exception ex)
        {
            Problem = ex.Message;
            System.Diagnostics.Debug.WriteLine($"[notifications] unavailable: {ex.Message}");
        }
    }

    /// <summary>Show a toast. One toast per tab: a newer one replaces the older (Tag/Group).</summary>
    public async Task ShowAsync(string title, string body, string attribution, string iconKey, NotificationTarget target, bool urgent)
    {
        if (!Available) return;
        var builder = new AppNotificationBuilder()
            .AddArgument("action", "open")
            .AddArgument("host", target.Host)
            .AddArgument("session", target.Session)
            .AddArgument("window", target.Window ?? "")
            .AddArgument("pane", target.Pane.ToString())
            .AddText(title)
            .SetAttributionText(attribution)
            .AddButton(new AppNotificationButton("Open")
                .AddArgument("action", "open")
                .AddArgument("host", target.Host)
                .AddArgument("session", target.Session)
                .AddArgument("window", target.Window ?? "")
                .AddArgument("pane", target.Pane.ToString()));
        if (!string.IsNullOrWhiteSpace(body)) builder.AddText(body);
        if (await TilePngAsync(ProgramIcons.Resolve(iconKey)) is { } logo) builder.SetAppLogoOverride(new Uri(logo), AppNotificationImageCrop.Default);
        if (urgent) builder.SetScenario(AppNotificationScenario.Urgent);
        else builder.MuteAudio();
        var notification = builder.BuildNotification();
        notification.Tag = Truncate(target.Window ?? target.Pane.ToString());
        notification.Group = Truncate(target.Session);
        if (urgent) notification.Priority = AppNotificationPriority.High;
        AppNotificationManager.Default.Show(notification);
    }

    /// <summary>The tab has been seen: withdraw its toast.</summary>
    public void Withdraw(string session, string window)
    {
        if (!Available) return;
        _ = AppNotificationManager.Default.RemoveByTagAndGroupAsync(Truncate(window), Truncate(session));
    }

    private static string Truncate(string s) => s.Length <= 64 ? s : s[..64];

    private void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        var a = args.Arguments;
        if (!a.TryGetValue("action", out var action) || action != "open") return;
        if (!a.TryGetValue("host", out var host) || !a.TryGetValue("session", out var session)) return;
        a.TryGetValue("window", out var window);
        ulong.TryParse(a.TryGetValue("pane", out var p) ? p : "0", out var pane);
        Invoked?.Invoke(this, new NotificationTarget(host, session, string.IsNullOrEmpty(window) ? null : window, pane));
    }

    /// <summary>A 48 px PNG of the program's tile, rendered once per key with Win2D and cached.</summary>
    private async Task<string?> TilePngAsync(ProgramIconSpec spec)
    {
        try
        {
            Directory.CreateDirectory(_cacheDir);
            string path = Path.Combine(_cacheDir, $"{spec.Key}-v1.png");
            if (File.Exists(path)) return path;
            var device = CanvasDevice.GetSharedDevice();
            using var target = new CanvasRenderTarget(device, 48, 48, 96);
            using (var ds = target.CreateDrawingSession())
            {
                ds.Clear(Color.FromArgb(0, 0, 0, 0));
                using var fill = new CanvasLinearGradientBrush(device, spec.HueLight, spec.Hue) { StartPoint = new(0, 0), EndPoint = new(0, 48) };
                ds.FillRoundedRectangle(0, 0, 48, 48, 11, 11, fill);
                using var sheen = new CanvasLinearGradientBrush(device, Color.FromArgb(0x38, 255, 255, 255), Color.FromArgb(0, 255, 255, 255)) { StartPoint = new(0, 0), EndPoint = new(0, 26) };
                ds.FillRoundedRectangle(0, 0, 48, 48, 11, 11, sheen);
                ds.DrawRoundedRectangle(0.75f, 0.75f, 46.5f, 46.5f, 10.5f, 10.5f, Color.FromArgb(0x26, 255, 255, 255), 1.5f);
                using var format = new CanvasTextFormat
                {
                    FontFamily = spec.Source switch
                    {
                        GlyphSource.Fluent => "Segoe Fluent Icons",
                        GlyphSource.Nerd => Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "SymbolsNerdFont-Regular.ttf") + "#Symbols Nerd Font",
                        _ => "Segoe UI Variable Display",
                    },
                    FontSize = spec.Source == GlyphSource.Monogram ? 22 : 28,
                    FontWeight = spec.Source == GlyphSource.Monogram ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                    HorizontalAlignment = CanvasHorizontalAlignment.Center,
                    VerticalAlignment = CanvasVerticalAlignment.Center,
                };
                ds.DrawText(spec.Glyph, new Windows.Foundation.Rect(0, 0, 48, 48), spec.Fg, format);
            }
            await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
            return path;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[notifications] tile render failed: {ex.Message}");
            return null;
        }
    }
}
