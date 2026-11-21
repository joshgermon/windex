// Settings pages (design §9.11), built from SettingsCard / SettingsExpander. Only settings the app
// actually honours are listed; the Terminal page's values are stored for the terminal view.
using CommunityToolkit.WinUI.Controls;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windex.Core;
using Windex.Services;
using Windex.Shell;
using Windex.Surfaces;
using Windows.ApplicationModel.DataTransfer;

namespace Windex.Settings;

public sealed record SettingsCardEntry(string Header, string? Description, FrameworkElement Element);

public sealed class SettingsPage(string key, FrameworkElement root, List<SettingsCardEntry> cards)
{
    public string Key { get; } = key;
    public FrameworkElement Root { get; } = root;
    public List<SettingsCardEntry> Cards { get; } = cards;
}

public static class SettingsPages
{
    public static readonly string[] Keys = ["general", "appearance", "terminal", "shortcuts", "hosts", "notifications", "about"];

    public static string Title(string key) => key switch
    {
        "general" => "General",
        "appearance" => "Appearance",
        "terminal" => "Terminal",
        "shortcuts" => "Shortcuts",
        "hosts" => "Remote hosts",
        "notifications" => "Notifications",
        _ => "About",
    };

    private static AppSettings S => App.Settings;

    public static SettingsPage Build(string key, SettingsWindow window)
    {
        var b = new Builder(key);
        b.Title(Title(key));
        switch (key)
        {
            case "general": General(b); break;
            case "appearance": Appearance(b); break;
            case "terminal": Terminal(b); break;
            case "shortcuts": ShortcutsPage(b); break;
            case "hosts": Hosts(b, window); break;
            case "notifications": NotificationsPage(b); break;
            default: About(b, window); break;
        }
        return b.Page;
    }

    // ------------------------------------------------------------------ pages

    private static void General(Builder b)
    {
        b.Section("Sessions");
        b.Card("Restore last session on launch", "Reopens the session you used last when its host connects. Never creates a session.", "\uE81C",
            Toggle(S.RestoreLastSession, v => S.Update("general", s => s.RestoreLastSession = v)));
        var confirm = Toggle(S.ConfirmClose, v => S.Update("confirm", s => s.ConfirmClose = v));
        b.Expander("Ask before closing tabs and panes", "Closing ends the programs on the server. \"Keep running\" is always the default.", "\uE7BA", confirm,
            b.Item("Don't ask when only a shell is running", null,
                Toggle(S.SkipConfirmWhenShellOnly, v => S.Update("confirm", s => s.SkipConfirmWhenShellOnly = v))));
        b.Section("Storage");
        var open = new Button { Content = "Open folder" };
        open.Click += async (_, _) => await Windows.System.Launcher.LaunchFolderPathAsync(Path.GetDirectoryName(S.FilePath)!);
        b.Card("Settings file", S.FilePath, "\uE8B7", open);
    }

    private static void Appearance(Builder b)
    {
        b.Section("Theme");
        b.Card("App theme", "Light and dark follow Windows unless you choose one here.", "\uE771",
            Combo(["Use system setting", "Light", "Dark"], ["system", "light", "dark"], S.Theme, v => S.Update("theme", s => s.Theme = v)));

        string effective = WindowMaterial.Effective(S);
        string materialNote = effective == "solid" && S.Material != "solid"
            ? "Windows has transparency effects, Energy saver or high contrast set so that Windex uses Solid."
            : "Mica Alt suits tabbed title bars. Acrylic shows the blurred desktop through the window.";
        var radios = new RadioButtons();
        AutomationProperties.SetName(radios, "Window material");
        string[] materials = ["mica-alt", "mica", "acrylic", "solid"];
        foreach (var label in new[] { "Mica Alt (recommended)", "Mica", "Acrylic", "Solid" }) radios.Items.Add(label);
        radios.SelectedIndex = Array.IndexOf(materials, S.Material);
        var glass = new Slider { Minimum = 40, Maximum = 100, StepFrequency = 5, Value = S.GlassOpacity, Width = 220, IsEnabled = S.Material == "acrylic" };
        glass.ValueChanged += (_, e) => S.Update("material", s => s.GlassOpacity = e.NewValue);
        var keep = Toggle(S.KeepGlassWhenInactive, v => S.Update("material", s => s.KeepGlassWhenInactive = v));
        keep.IsEnabled = S.Material == "acrylic";
        radios.SelectionChanged += (_, _) =>
        {
            if (radios.SelectedIndex < 0) return;
            string m = materials[radios.SelectedIndex];
            glass.IsEnabled = keep.IsEnabled = m == "acrylic";
            S.Update("material", s => s.Material = m);
        };
        var material = b.Expander("Window material", materialNote, "\uE7F4", null,
            new SettingsCard { ContentAlignment = ContentAlignment.Left, Content = radios },
            b.Item("Glass opacity", "Acrylic only. Lower is more see-through.", glass),
            b.Item("Keep glass when the window is inactive", "Acrylic only.", keep));
        material.IsExpanded = true;

        b.Card("Workspace tint", "The gradient ground behind the cards.", "\uE790",
            Combo(["Aurora", "Accent", "None"], ["aurora", "accent", "none"], S.Tint, v => S.Update("tint", s => s.Tint = v)));

        b.Section("Panes");
        var style = new Segmented();
        style.Items.Add(new SegmentedItem { Content = "Comfortable" });
        style.Items.Add(new SegmentedItem { Content = "Compact" });
        style.SelectedIndex = S.PaneStyle == "compact" ? 1 : 0;
        style.SelectionChanged += (_, _) => S.Update("panes", s => s.PaneStyle = style.SelectedIndex == 1 ? "compact" : "comfortable");
        AutomationProperties.SetName(style, "Pane style");
        b.Card("Pane style", "Comfortable floats each pane on its own glass card. Compact tiles them in one frame with hairline gaps.", "\uF0E2", style);
        b.Card("Pane headers", "The row with each pane's title and buttons. On hover keeps it out of the way until you rest the pointer at the top of a pane.", "\uE8A1",
            Combo(["Always", "On hover", "Hidden"], ["always", "hover", "hidden"], S.PaneHeaders, v => S.Update("panes", s => s.PaneHeaders = v)));
        b.Card("Dim inactive panes", "Unfocused cards get a more transparent surface and a dimmer terminal.", "\uE793",
            Combo(["Off", "Surface", "Content", "Surface and content"], ["off", "surface", "content", "both"], S.DimInactive, v => S.Update("panes", s => s.DimInactive = v)));
        b.Card("Highlight the focused pane's edges", "An accent outline around the focused card. It is lifted and brighter either way.", "\uE7B3",
            Toggle(S.HighlightFocused, v => S.Update("panes", s => s.HighlightFocused = v)));

        b.Section("Tabs and sidebar");
        var icons = new Slider { Minimum = 0, Maximum = 5, StepFrequency = 1, TickFrequency = 1, TickPlacement = Microsoft.UI.Xaml.Controls.Primitives.TickPlacement.Outside, Value = S.TabIconCount, Width = 220 };
        icons.ValueChanged += (_, e) => S.Update("tabs", s => s.TabIconCount = (int)e.NewValue);
        b.Card("Tab icons", "How many program tiles a tab shows, focused pane first.", "\uE8B9", icons);
        b.Expander("Colourful icons", "Programs get their own tile in tabs, pane headers, the sidebar and the switcher.", "\uE790",
            Combo(["Always", "Active tab only", "Never"], ["always", "active", "never"], S.ColorfulIcons, v => S.Update("tabs", s => s.ColorfulIcons = v)),
            new SettingsCard { ContentAlignment = ContentAlignment.Left, Content = IconSheet() });
        var side = new Segmented();
        side.Items.Add(new SegmentedItem { Content = "Left" });
        side.Items.Add(new SegmentedItem { Content = "Right" });
        side.SelectedIndex = S.SidebarPosition == "right" ? 1 : 0;
        side.SelectionChanged += (_, _) => S.Update("sidebar", s => s.SidebarPosition = side.SelectedIndex == 1 ? "right" : "left");
        b.Card("Sidebar position", "Left is the Windows convention; Right matches Rex on Mac.", "\uE90C", side);
        b.Card("Animations", "Chrome motion only; terminal content never animates. Natural is half again as slow as Snappy. Windows' Animation effects setting turns them off too.", "\uE916",
            Combo(["Snappy", "Natural", "Off"], ["snappy", "natural", "off"], S.Animations, v => S.Update("panes", s => s.Animations = v)));
    }

    private static void Terminal(Builder b)
    {
        b.Section("Font");
        var families = MonospacedFamilies();
        var font = new ComboBox { Width = 240, IsEditable = true, Text = S.TerminalFontFamily };
        foreach (var f in families) font.Items.Add(new ComboBoxItem { Content = f, FontFamily = new FontFamily(f), Tag = f });
        font.SelectedItem = font.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == S.TerminalFontFamily);
        var preview = new Border
        {
            Padding = new Thickness(14, 10, 14, 10), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 4, 0, 0),
            Background = ThemeLookup.Brush(ThemeLookup.Theme, "RexPreviewWellBrush"),
        };
        var sample = new TextBlock { Text = "joshua@dasher ~/src/windex (main)\n❯ cargo test -p rexcore\n   Compiling rexcore v0.1.0 · 0O1lI {}[]()=>", LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
        preview.Child = sample;
        void Sample()
        {
            sample.FontFamily = new FontFamily(S.TerminalFontFamily + ", Cascadia Mono, Consolas");
            sample.FontSize = S.TerminalFontSize * 96 / 72;
            sample.LineHeight = Math.Ceiling(sample.FontSize * S.TerminalLineHeight);
        }
        Sample();
        void SetFont(string name) { if (name.Trim().Length > 0) { S.Update("terminal", s => s.TerminalFontFamily = name.Trim()); Sample(); } }
        font.SelectionChanged += (_, _) => { if (font.SelectedItem is ComboBoxItem { Tag: string f }) SetFont(f); };
        font.TextSubmitted += (_, e) => SetFont(e.Text);
        b.Card("Font family", "Monospaced fonts installed on this PC. Cascadia Mono ships with Windows 11.", "\uE8D2", font);
        var size = new NumberBox { Minimum = 6, Maximum = 36, Value = S.TerminalFontSize, SmallChange = 0.5, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 140 };
        size.ValueChanged += (_, e) => { if (!double.IsNaN(e.NewValue)) { S.Update("terminal", s => s.TerminalFontSize = e.NewValue); Sample(); } };
        b.Card("Size", "Points (6–36).", "\uE8E9", size);
        var line = new NumberBox { Minimum = 1.0, Maximum = 2.0, Value = S.TerminalLineHeight, SmallChange = 0.05, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 140 };
        line.ValueChanged += (_, e) => { if (!double.IsNaN(e.NewValue)) { S.Update("terminal", s => s.TerminalLineHeight = Math.Round(e.NewValue, 2)); Sample(); } };
        b.Card("Line height", "Multiple of the font size (1.0–2.0).", "\uE8E3", line);
        b.Add(preview, "Preview", null);

        b.Section("Cursor");
        b.Card("Cursor shape", "Used until a program picks its own shape.", "\uE8AC",
            Combo(["Block", "Bar", "Underline", "Hollow block"], ["block", "bar", "underline", "hollow"], S.CursorShape, v => S.Update("terminal", s => s.CursorShape = v)));
        b.Card("Blinking cursor", "Blinks only in the focused pane.", "\uE7C8",
            Toggle(S.CursorBlink, v => S.Update("terminal", s => s.CursorBlink = v)));

        b.Section("Clipboard");
        b.Card("Copy on select", "Selecting text copies it, as in many Linux terminals. Ctrl+Shift+C copies either way.", "\uE8C8",
            Toggle(S.CopyOnSelect, v => S.Update("terminal", s => s.CopyOnSelect = v)));
        b.Card("Warn before pasting multiple lines", "Asks first when a paste would run several commands in a program that isn't expecting a paste.", "\uE7BA",
            Toggle(S.WarnMultilinePaste, v => S.Update("terminal", s => s.WarnMultilinePaste = v)));
    }

    private static void ShortcutsPage(Builder b)
    {
        var filter = new AutoSuggestBox { PlaceholderText = "Filter shortcuts", QueryIcon = new SymbolIcon(Symbol.Find), Margin = new Thickness(0, 0, 0, 4) };
        b.Add(filter, "Filter shortcuts", null);
        b.Card("Use Ctrl+K for the session switcher", "Programs in the terminal will no longer receive Ctrl+K.", "\uE8AB",
            Toggle(S.CtrlKSwitcher, v => S.Update("shortcuts", s => s.CtrlKSwitcher = v)));
        b.Note("Shortcuts follow Windows Terminal conventions. Keys the app doesn't use reach the program in the focused pane.");
        var list = new StackPanel { Spacing = 4 };
        b.Add(list, "Shortcuts", null);
        // Probed once per page: another app's global hotkey swallows a chord before Windex sees it.
        var takenCache = new Dictionary<KeyChord, bool>();
        bool TakenElsewhere(KeyChord chord) =>
            takenCache.TryGetValue(chord, out var t) ? t : takenCache[chord] = Native.HotkeyTakenElsewhere(chord);
        void Fill()
        {
            list.Children.Clear();
            string q = filter.Text.Trim();
            foreach (var group in Shortcuts.All.Where(s => !s.Id.StartsWith("tab-") || s.Id is "tab-1" or "tab-9").GroupBy(s => s.Group))
            {
                var rows = group.Where(s => q.Length == 0 || RexPalette.Matches(s.Label, q) || RexPalette.Matches(group.Key, q)
                                            || s.Keys.Any(k => k.ToString().Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
                if (rows.Count == 0) continue;
                list.Children.Add(new TextBlock { Text = group.Key, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(1, list.Children.Count == 0 ? 8 : 20, 0, 4) });
                foreach (var s in rows)
                {
                    var caps = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
                    var taken = new List<KeyChord>();
                    foreach (var chord in s.Keys)
                    {
                        var one = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                        foreach (var cap in chord.Caps) one.Children.Add(RexPalette.Keycap(cap));
                        if (TakenElsewhere(chord))
                        {
                            // Windows hands this chord to another app's global hotkey before Windex sees it.
                            taken.Add(chord);
                            one.Opacity = 0.4;
                            ToolTipService.SetToolTip(one, $"{chord} is taken by another app on this PC");
                        }
                        caps.Children.Add(one);
                    }
                    var card = new SettingsCard { Header = s.Label, Content = caps, MinHeight = 48 };
                    if (taken.Count > 0)
                    {
                        var free = s.Keys.Except(taken).ToList();
                        card.Description = $"{string.Join(" and ", taken)} {(taken.Count > 1 ? "are" : "is")} taken by another app on this PC, so Windex never receives {(taken.Count > 1 ? "them" : "it")}."
                            + (free.Count > 0 ? $" Use {string.Join(" or ", free)}." : "");
                    }
                    list.Children.Add(card);
                }
            }
        }
        filter.TextChanged += (_, _) => Fill();
        Fill();
    }

    private static void Hosts(Builder b, SettingsWindow window)
    {
        var add = new Button { Content = "Add host", Style = (Style)Application.Current.Resources["AccentButtonStyle"], Margin = new Thickness(0, 0, 0, 8) };
        add.Click += async (_, _) =>
        {
            if (window.DialogRoot is not { } root || App.Main is not { } main) return;
            await AddHostDialog.ShowAsync(root, App.Core, App.Hosts, main.Model, main.Tailnet, null, null);
            window.Refresh();
        };
        b.Add(add, "Add host", null);
        if (App.Hosts.IsDemo)
            b.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational, Title = "Demo hosts", Message = "Windex is showing demo data. Hosts added now are not saved." }, "Demo hosts", null);

        var tailnet = App.Main?.Tailnet ?? [];
        string tailscale = tailnet.Count == 0 ? "Tailscale isn't running on this PC, or found no machines."
            : $"{tailnet.Count(m => !m.Self)} machines on your tailnet · {tailnet.Count(m => m.Rex is not null)} with a Rex server";
        b.Card("Tailscale", tailscale, "\uE968", null);

        b.Section("Hosts");
        var model = App.Main?.Model ?? RexModel.Empty;
        if (App.Hosts.Hosts.Count == 0) b.Note("No hosts yet. Add a Rex server on your tailnet or over SSH.");
        foreach (var saved in App.Hosts.Hosts.ToList())
        {
            var live = model.Host(saved.Id);
            string? trouble = live?.State switch { "reconnecting" => "Reconnecting", "offline" => "Offline", "error" => "Can't connect", "connecting" => "Connecting", _ => null };
            var kind = HostKinds.Get(saved.Icon);
            var edit = new Button { Content = "Edit…" };
            edit.Click += async (_, _) =>
            {
                if (window.DialogRoot is not { } root || App.Main is not { } main) return;
                await AddHostDialog.ShowAsync(root, App.Core, App.Hosts, main.Model, main.Tailnet, null, saved);
                window.Refresh();
            };
            var remove = new Button { Content = "Remove host…", Style = (Style)Application.Current.Resources["RexCriticalButtonStyle"] };
            remove.Click += async (_, _) =>
            {
                if (window.DialogRoot is not { } root) return;
                var dialog = new ContentDialog
                {
                    XamlRoot = root,
                    Title = $"Remove {saved.Name}?",
                    Content = $"Windex forgets this host. Nothing changes on {saved.Name}: its sessions keep running.",
                    PrimaryButtonText = "Remove host",
                    PrimaryButtonStyle = (Style)Application.Current.Resources["RexCriticalButtonStyle"],
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                    Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    await App.Hosts.RemoveAsync(saved.Id);
                    window.Refresh();
                }
            };
            var expander = b.Expander(saved.Name, trouble is null ? saved.Endpoint : $"{saved.Endpoint} · {trouble}", kind, edit,
                b.Item("Address", null, new TextBlock { Text = saved.Endpoint, IsTextSelectionEnabled = true, VerticalAlignment = VerticalAlignment.Center }),
                b.Item("Server", null, new TextBlock { Text = live?.Server is { } server ? $"Rex {server.Version} · API {server.Api}" : "Not reported", VerticalAlignment = VerticalAlignment.Center }),
                b.Item("Connect at launch", null, Toggle(saved.ConnectAtLaunch, v => App.Hosts.SetConnectAtLaunch(saved.Id, v))),
                b.Item("Remove", "Forget this host. Sessions on it keep running.", remove));
        }
    }

    private static void NotificationsPage(Builder b)
    {
        var open = new HyperlinkButton { Content = "Open Windows notification settings", NavigateUri = new Uri("ms-settings:notifications") };
        b.Card("Windows notifications for Windex",
            App.Notifications.Available ? "Toasts appear when a tab you can't see needs you." : $"Windows notifications aren't available: {App.Notifications.Problem ?? "not supported here"}.",
            "\uEA8F", open);
        b.Section("Notify me when");
        b.Card("A program needs input", "Approvals, questions and sign-ins. High priority, with sound.", "\uE7BA",
            Toggle(S.NotifyNeedsInput, v => S.Update("notifications", s => s.NotifyNeedsInput = v)));
        b.Card("A program hits an error", null, "\uE783",
            Toggle(S.NotifyError, v => S.Update("notifications", s => s.NotifyError = v)));
        b.Card("A program is done", "Silent.", "\uE73E",
            Toggle(S.NotifyDone, v => S.Update("notifications", s => s.NotifyDone = v)));
        b.Card("A program rings the bell", "Silent.", "\uEA8F",
            Toggle(S.NotifyBell, v => S.Update("notifications", s => s.NotifyBell = v)));
        b.Card("A program sends a notification", "OSC 9 and OSC 777 messages. Silent.", "\uE8BD",
            Toggle(S.NotifyProgramMessages, v => S.Update("notifications", s => s.NotifyProgramMessages = v)));
        b.Section("Taskbar");
        b.Card("Progress and badge on the taskbar button", "The session's progress fills the button; a warning badge means a tab needs you, an error badge that one failed.", "\uE9D9",
            Toggle(S.TaskbarProgress, v => S.Update("notifications", s => s.TaskbarProgress = v)));
        b.Card("Flash the taskbar button", "When a program needs input and Windex is in the background.", "\uE7C4",
            Toggle(S.FlashTaskbar, v => S.Update("notifications", s => s.FlashTaskbar = v)));
        var test = new Button { Content = "Send test notification", IsEnabled = App.Notifications.Available };
        test.Click += async (_, _) =>
        {
            var model = App.Main?.Model ?? RexModel.Empty;
            var host = model.Host(model.Current?.Host);
            var session = model.CurrentSession;
            var window = session?.Windows.FirstOrDefault(w => w.Id == session.ActiveWindow);
            await App.Notifications.ShowAsync("Test notification", "This is how Windex tells you a tab needs you.",
                session is null || host is null ? "Windex" : $"{session.Label} · {host.Name}", "rex",
                new NotificationTarget(host?.Id ?? "", session?.Id ?? "", window?.Id, window?.FocusedPane ?? 0), urgent: false);
        };
        b.Card("Send test notification", null, "\uE724", test);
    }

    private static void About(Builder b, SettingsWindow window)
    {
        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        var model = App.Main?.Model ?? RexModel.Empty;
        var host = model.Host(model.Current?.Host) ?? model.Hosts.FirstOrDefault(h => h.Server is not null);
        string server = host?.Server is { Version: { } v } ? $"Rex Server {v} on {host.Name}" : "Rex Server: not reported";
        string core = App.Core is MockCore ? "Demo data (MockCore)" : "rexcore.dll";

        var hero = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Spacing = 4, Margin = new Thickness(0, 8, 0, 20) };
        hero.Children.Add(new Image { Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Windex-256.png"))), Width = 64, Height = 64, Margin = new Thickness(0, 0, 0, 8) });
        hero.Children.Add(new TextBlock { Text = "Windex", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], HorizontalAlignment = HorizontalAlignment.Center });
        hero.Children.Add(new TextBlock { Text = $"Version {version} · {core}", HorizontalAlignment = HorizontalAlignment.Center });
        hero.Children.Add(new TextBlock { Text = server, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Opacity = 0.75, HorizontalAlignment = HorizontalAlignment.Center });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
        var copy = new Button { Content = "Copy version info" };
        copy.Click += (_, _) =>
        {
            var package = new DataPackage();
            package.SetText($"Windex {version} ({core})\n{server}\nWindows {Environment.OSVersion.Version}");
            Clipboard.SetContent(package);
        };
        var logs = new Button { Content = "Open logs folder" };
        logs.Click += async (_, _) => { Directory.CreateDirectory(App.LogDirectory); await Windows.System.Launcher.LaunchFolderPathAsync(App.LogDirectory); };
        buttons.Children.Add(copy);
        buttons.Children.Add(logs);
        hero.Children.Add(buttons);
        b.Add(hero, "Windex", null);

        var docs = new HyperlinkButton { Content = "superlogical.com/rex", NavigateUri = new Uri("https://www.superlogical.com/rex") };
        b.Card("Documentation", "Rex is Superlogical's terminal multiplexer; Windex is a Windows client for it.", "\uE736", docs);
        var notices = new Button { Content = "View" };
        notices.Click += async (_, _) =>
        {
            if (window.DialogRoot is not { } root) return;
            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = "Third-party notices",
                CloseButtonText = "Close",
                Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
                Content = new ScrollViewer
                {
                    MaxHeight = 360,
                    Content = new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Text = "Win2D — MIT License, Microsoft.\n\n"
                             + "Windows Community Toolkit (SettingsControls, Segmented) and CommunityToolkit.Mvvm — MIT License, .NET Foundation.\n\n"
                             + "Symbols Nerd Font 3.5.1 — MIT License, Ryan L McIntyre. Glyph sets inside it keep their own licences: "
                             + "Devicons (MIT), Codicons (CC BY 4.0), Font Awesome (CC BY 4.0), Material Design Icons (Apache 2.0), Octicons (MIT), Seti UI (MIT). "
                             + "Logos are trademarks of their owners and identify programs only.\n\n"
                             + "Cascadia Mono and Segoe Fluent Icons — system fonts, referenced and not redistributed.\n\n"
                             + "Program-name mapping logic adapted from Rex Rune (MIT).",
                    },
                },
            };
            await dialog.ShowAsync();
        };
        b.Card("Third-party notices", null, "\uE8A5", notices);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Every program tile with its name; the tooltip lists the commands that show it.</summary>
    private static FrameworkElement IconSheet()
    {
        var grid = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 84, ItemHeight = 64, Margin = new Thickness(0, 4, 0, 4) };
        foreach (var spec in ProgramIcons.All)
        {
            var cell = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            cell.Children.Add(new ProgramTile { IconKey = spec.Key, TileSize = 28, HorizontalAlignment = HorizontalAlignment.Center });
            cell.Children.Add(new TextBlock { Text = spec.Key, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.8 });
            var names = ProgramIcons.NamesFor(spec.Key).Take(12).ToList();
            if (names.Count > 0) ToolTipService.SetToolTip(cell, string.Join(", ", names));
            AutomationProperties.SetName(cell, names.Count > 0 ? $"{spec.Key}: {string.Join(", ", names)}" : spec.Key);
            grid.Children.Add(cell);
        }
        return grid;
    }

    private static IReadOnlyList<string>? _monospaced;

    /// <summary>Installed monospaced families (DirectWrite through Win2D), read once.</summary>
    private static IReadOnlyList<string> MonospacedFamilies()
    {
        if (_monospaced is not null) return _monospaced;
        try
        {
            using var set = CanvasFontSet.GetSystemFontSet();
            _monospaced = set.Fonts.Where(f => f.IsMonospaced)
                .Select(f => f.FamilyNames.TryGetValue("en-us", out var n) ? n : f.FamilyNames.Values.FirstOrDefault())
                .Where(n => !string.IsNullOrEmpty(n)).Select(n => n!)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();
        }
        catch (Exception ex)
        {
            App.Log($"font enumeration failed: {ex.Message}");
            _monospaced = ["Cascadia Mono", "Cascadia Code", "Consolas", "Courier New", "Lucida Console"];
        }
        return _monospaced;
    }

    private static ToggleSwitch Toggle(bool value, Action<bool> set)
    {
        var toggle = new ToggleSwitch { IsOn = value, OnContent = "On", OffContent = "Off" };
        toggle.Toggled += (_, _) => set(toggle.IsOn);
        return toggle;
    }

    private static ComboBox Combo(string[] labels, string[] values, string current, Action<string> set)
    {
        var combo = new ComboBox { MinWidth = 200 };
        foreach (var l in labels) combo.Items.Add(l);
        combo.SelectedIndex = Math.Max(0, Array.IndexOf(values, current));
        combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) set(values[combo.SelectedIndex]); };
        return combo;
    }

    private sealed class Builder(string key)
    {
        private readonly StackPanel _root = new() { Spacing = 4 };
        private readonly List<SettingsCardEntry> _cards = new();
        private bool _firstSection = true;

        public SettingsPage Page => new(key, _root, _cards);

        public void Title(string text) =>
            _root.Children.Add(new TextBlock { Text = text, Style = (Style)Application.Current.Resources["TitleTextBlockStyle"], Margin = new Thickness(1, 0, 0, 12) });

        public void Section(string text)
        {
            _root.Children.Add(new TextBlock
            {
                Text = text,
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                Margin = new Thickness(1, _firstSection ? 8 : 28, 0, 4),
            });
            _firstSection = false;
        }

        public void Note(string text) =>
            _root.Children.Add(new TextBlock { Text = text, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(1, 4, 0, 8) });

        public void Add(FrameworkElement element, string header, string? description)
        {
            _root.Children.Add(element);
            _cards.Add(new SettingsCardEntry(header, description, element));
        }

        public SettingsCard Card(string header, string? description, string glyph, object? content)
        {
            // The toolkit types these as object; null means "none".
            var card = new SettingsCard { Header = header, Description = description!, HeaderIcon = new FontIcon { Glyph = glyph }, Content = content! };
            Add(card, header, description);
            return card;
        }

        public SettingsCard Item(string header, string? description, object content) =>
            new() { Header = header, Description = description!, Content = content };

        public SettingsExpander Expander(string header, string? description, string glyph, object? content, params SettingsCard[] items) =>
            Expander(header, description, new FontIcon { Glyph = glyph }, content, items);

        public SettingsExpander Expander(string header, string? description, HostKind kind, object? content, params SettingsCard[] items) =>
            Expander(header, description, new FontIcon { Glyph = kind.Glyph, FontFamily = ProgramIcons.FontFor(kind.Source) }, content, items);

        private SettingsExpander Expander(string header, string? description, IconElement icon, object? content, SettingsCard[] items)
        {
            var expander = new SettingsExpander { Header = header, Description = description!, HeaderIcon = icon, Content = content! };
            foreach (var item in items) expander.Items.Add(item);
            Add(expander, header, description);
            return expander;
        }
    }
}
