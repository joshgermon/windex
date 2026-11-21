// The Settings window: one instance, NavigationView over pages built by SettingsPages, and a
// search over every card's header and description that jumps to the card and highlights it.
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windex.Shell;
using Windows.Graphics;

namespace Windex.Settings;

public sealed partial class SettingsWindow : Window
{
    private readonly List<SettingsIndexEntry> _index = new();
    private SettingsPage? _page;
    private bool _navigating;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _highlightTimer;
    private Action? _endHighlight;

    private void EndHighlight()
    {
        _highlightTimer?.Stop();
        _endHighlight?.Invoke();
        _endHighlight = null;
    }

    public SettingsWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        // The TitleBar control names the window after its own Title; the taskbar and Alt+Tab
        // should still say which app's settings these are.
        AppTitleBar.Loaded += (_, _) => AppWindow.Title = "Settings – Windex";
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "Windex.ico");
        AppWindow.SetIcon(icon);
        TitleIcon.ImageSource = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Windex-256.png")));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 720;
            presenter.PreferredMinimumHeight = 520;
        }
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = GetDpiForWindow(hwnd) / 96.0;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int w = Math.Min((int)(1040 * scale), area.Width - 32), h = Math.Min((int)(720 * scale), area.Height - 32);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2 + (int)(40 * scale), area.Y + (area.Height - h) / 2 + (int)(30 * scale), w, h));

        ApplyTheme();
        App.Settings.Changed += OnSettingsChanged;
        Closed += (_, _) => App.Settings.Changed -= OnSettingsChanged;

        foreach (var key in SettingsPages.Keys)
            foreach (var card in SettingsPages.Build(key, this).Cards)
                _index.Add(new SettingsIndexEntry(key, card.Header, card.Description));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    public XamlRoot? DialogRoot => Content.XamlRoot;

    private void OnSettingsChanged(object? sender, string key)
    {
        if (key == "theme") ApplyTheme();
    }

    private void ApplyTheme() => Root.RequestedTheme = App.Settings.Theme switch
    {
        "light" => ElementTheme.Light,
        "dark" => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    /// <summary>Show a page by key (null keeps the current one, or opens General).</summary>
    public void Navigate(string? key, string? card = null)
    {
        key ??= _page?.Key ?? "general";
        var item = Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == key);
        _navigating = true;
        Nav.SelectedItem = item;
        _navigating = false;
        Show(key, card);
    }

    /// <summary>Rebuild the current page (after hosts change, for instance).</summary>
    public void Refresh()
    {
        if (_page is null) return;
        double offset = Scroller.VerticalOffset;
        Show(_page.Key, null, transition: false);
        Scroller.ChangeView(null, offset, null, disableAnimation: true);
    }

    private void Show(string key, string? card, bool transition = true)
    {
        _page = SettingsPages.Build(key, this);
        var transitions = PageHost.ContentTransitions;
        if (!transition) PageHost.ContentTransitions = null;
        PageHost.Content = _page.Root;
        if (!transition) PageHost.ContentTransitions = transitions;
        if (card is null) { Scroller.ChangeView(null, 0, null, disableAnimation: true); return; }
        var target = _page.Cards.FirstOrDefault(c => c.Header == card);
        if (target is null) return;
        target.Element.Loaded += (_, _) => Highlight(target.Element);
    }

    /// <summary>Scroll a card into view and wash it with the subtle fill for 2.4 s.</summary>
    private void Highlight(FrameworkElement element)
    {
        element.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.3, AnimationDesired = false });
        if (element is Control control)
        {
            EndHighlight();
            var original = control.Background;
            control.Background = ThemeLookup.Brush(Root, "RexSubtleHoverBrush");
            _endHighlight = () => control.Background = original;
            // A field, not a local: a timer only its Tick handler refers to can be collected before it fires.
            if (_highlightTimer is null)
            {
                _highlightTimer = DispatcherQueue.CreateTimer();
                _highlightTimer.Interval = TimeSpan.FromSeconds(2.4);
                _highlightTimer.IsRepeating = false;
                _highlightTimer.Tick += (_, _) => EndHighlight();
            }
            _highlightTimer.Start();
        }
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_navigating || args.SelectedItem is not NavigationViewItem { Tag: string key }) return;
        Show(key, null);
    }

    private void SettingsSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        string q = sender.Text.Trim();
        sender.ItemsSource = q.Length == 0 ? null
            : _index.Where(e => e.Header.Contains(q, StringComparison.OrdinalIgnoreCase) || (e.Description?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
                .Take(12).ToList();
    }

    private void SettingsSearch_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is SettingsIndexEntry entry) { sender.Text = ""; Navigate(entry.Page, entry.Header); }
    }

    private void SettingsSearch_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var entry = args.ChosenSuggestion as SettingsIndexEntry
                    ?? _index.FirstOrDefault(e => e.Header.Contains(args.QueryText, StringComparison.OrdinalIgnoreCase));
        if (entry is not null) { sender.Text = ""; Navigate(entry.Page, entry.Header); }
    }
}

public sealed record SettingsIndexEntry(string Page, string Header, string? Description)
{
    public override string ToString() => $"{Header} — {SettingsPages.Title(Page)}";
}
