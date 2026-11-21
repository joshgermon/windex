// The main window: wires the core's model into the title row, sidebar and pane area, and owns
// window-level concerns (title bar, material, theme, sidebar layout, notices, notifications).
// Actions live in MainWindow.Actions.cs, the title row in MainWindow.TitleBar.cs and keyboard
// shortcuts in MainWindow.Keys.cs.
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windex.Core;
using Windex.Panes;
using Windex.Services;
using Windex.Shell;
using Windex.Surfaces;
using Windows.Graphics;

namespace Windex;

public sealed partial class MainWindow : Window
{
    private readonly ICore _core = App.Core;
    private readonly AppSettings _settings = App.Settings;
    private readonly HostRegistry _hosts = App.Hosts;
    private readonly PaneSplitter _sidebarGrip = new("sidebar");
    private RexModel _model = RexModel.Empty;
    private IReadOnlyList<DiscoveredMachine> _tailnet = [];
    private bool _windowActive;
    private bool? _appFocusSent;
    /// <summary>The last session is still to be reopened (its host has not connected yet).</summary>
    private bool _restorePending = true;
    /// <summary>
    /// The session the user opened (or restore reopened). The core makes a session current on its
    /// own when a host connects; the window shows that one too but only claims a chosen one.
    /// </summary>
    private CurrentRef? _chosen;
    /// <summary>The chosen session has been current at least once (it is no longer in flight).</summary>
    private bool _chosenShown;
    private CurrentRef? _remembered;
    private string? _troubleHost;
    private string? _dismissedTrouble;
    private bool _overlayOpen;
    private bool _paneFocusedOnce;
    /// <summary>A pane action is in flight: give the focused pane keyboard focus on the next model.</summary>
    private bool _refocusPane;
    /// <summary>The last focus_pane sent and when, so one click does not send it several times.</summary>
    private (ulong Pane, DateTime At)? _focusRequest;
    /// <summary>The terminal theme last declared to the core (dark or light), if any.</summary>
    private bool? _declaredDark;
    private InfoBar? _fontNotice;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _fontNoticeTimer;
    private readonly Taskbar _taskbar;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _sidebarCollapse;
    private readonly HashSet<Microsoft.UI.Dispatching.DispatcherQueueTimer> _noticeTimers = new();

    public nint Hwnd { get; }

    /// <summary>The latest model snapshot (Settings reads it for host and server details).</summary>
    public RexModel Model => _model;

    /// <summary>The latest tailnet discovery.</summary>
    public IReadOnlyList<DiscoveredMachine> Tailnet => _tailnet;

    public MainWindow()
    {
        InitializeComponent();
        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _taskbar = new Taskbar(Hwnd);

        SetupWindow();
        SetupTitleBar();
        PaneArea.Attach(_core, this);
        SidebarView.Attach(this);
        SidebarView.AddHostTarget.SizeChanged += (_, _) => RenderAddHostTip();
        SetupSidebarGrip();
        // The overlay sidebar is light-dismissed by any press in the workspace.
        Workspace.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) =>
        {
            if (SidebarIsOverlay && _overlayOpen) { _overlayOpen = false; ApplySidebarLayout(animate: true); }
        }), handledEventsToo: true);

        _core.ModelChanged += (_, model) => OnModel(model);
        _core.NoticeRaised += (_, notice) => ShowNotice(notice.Level, notice.Title, notice.Message);
        _core.NotificationRaised += (_, note) => OnDesktopNotification(note);
        _core.DiscoveryUpdated += (_, machines) => { _tailnet = machines; Render(); };
        _hosts.Changed += (_, _) => Render();
        _settings.Changed += (_, key) => OnSettingChanged(key);
        App.Notifications.Invoked += (_, target) => DispatcherQueue.TryEnqueue(() => OpenTarget(target));
        WindowMaterial.SystemChanged += (_, _) => DispatcherQueue.TryEnqueue(ApplyMaterial);

        Activated += OnActivated;
        Root.ActualThemeChanged += (_, _) => { ThemeLookup.Theme = Root.ActualTheme; ApplyCaptionColors(); PaintTitleButtons(); DeclareTerminalTheme(); Render(); };
        Root.SizeChanged += (_, _) => { ApplyResponsive(); ApplySidebarLayout(animate: false); };
        Closed += (_, _) => _settings.Save();

        ApplyTheme();
        DeclareTerminalTheme();
        ApplyPaneStyle();
        ApplyMaterial();
        ApplySidebarLayout(animate: false);
        OnModel(_core.Model);
        _ = _core.CommandAsync("discover");
    }

    // ------------------------------------------------------------------ window setup

    private void SetupWindow()
    {
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Windex.ico"));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 640;
            presenter.PreferredMinimumHeight = 480;
        }
        double scale = GetDpiForWindow(Hwnd) / 96.0;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int w = Math.Min((int)(1280 * scale), area.Width - 32), h = Math.Min((int)(800 * scale), area.Height - 32);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        bool active = e.WindowActivationState != WindowActivationState.Deactivated;
        if (active == _windowActive) return;
        _windowActive = active;
        SyncAppFocus();
        PaintTitleButtons();
        if (active && CurrentSession is { } s && CurrentWindow is { } w) App.Notifications.Withdraw(s.Id, w.Id);
        Render();
    }

    /// <summary>Title-bar glyphs: primary text while active, disabled text while inactive (design §3.2).</summary>
    private void PaintTitleButtons()
    {
        var brush = ThemeLookup.Brush(Root, _windowActive ? "RexPrimaryTextBrush" : "RexDisabledTextBrush");
        foreach (var b in new[] { SidebarToggle, ActivityButton, SearchButton, SettingsButton, MoreActionsButton }) b.Foreground = brush;
    }

    private void ApplyTheme()
    {
        Root.RequestedTheme = _settings.Theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        ThemeLookup.Theme = Root.ActualTheme;
        ApplyCaptionColors();
    }

    /// <summary>
    /// Declare Rex Dusk (dark) or Rex Dawn (light) to the core, which passes it to the server for
    /// the panes this window owns; the server applies it to the terminal, and frames carry it back.
    /// </summary>
    private void DeclareTerminalTheme()
    {
        bool dark = Root.ActualTheme == ElementTheme.Dark;
        if (_declaredDark == dark) return;
        _declaredDark = dark;
        _ = _core.CommandAsync("set_theme", new { theme = Windex.Terminal.TerminalTheme.ForAppTheme(dark).ToCoreTheme() });
    }

    /// <summary>Ctrl+= / Ctrl+- / Ctrl+0 and Ctrl+wheel: terminal font size, with one notice that updates in place.</summary>
    public void AdjustFontSize(int step)
    {
        double size = step == 0 ? AppSettings.DefaultTerminalFontSize : Math.Clamp(Math.Round(_settings.TerminalFontSize) + step, 6, 36);
        if (size != _settings.TerminalFontSize) _settings.Update("terminal", s => s.TerminalFontSize = size);
        string title = $"Font {size:0.#} pt";
        if (_fontNotice is null || !Notices.Children.Contains(_fontNotice))
        {
            _fontNotice = new InfoBar
            {
                Severity = InfoBarSeverity.Informational,
                IsClosable = false,
                IsOpen = true,
                Background = ThemeLookup.Brush(Root, "RexOverlayBrush"),
            };
            Notices.Children.Add(_fontNotice);
            _fontNoticeTimer = DispatcherQueue.CreateTimer();
            _fontNoticeTimer.Interval = TimeSpan.FromSeconds(1.5);
            _fontNoticeTimer.IsRepeating = false;
            _fontNoticeTimer.Tick += (_, _) => { if (_fontNotice is not null) Notices.Children.Remove(_fontNotice); _fontNotice = null; };
        }
        _fontNotice.Title = title;
        AutomationProperties.SetName(_fontNotice, title);
        _fontNoticeTimer?.Stop();
        _fontNoticeTimer?.Start();
    }

    /// <summary>System caption buttons follow our theme, not just the system's (design §3.2).</summary>
    private void ApplyCaptionColors()
    {
        var bar = AppWindow.TitleBar;
        bool dark = Root.ActualTheme == ElementTheme.Dark;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = dark ? Colors.White : Windows.UI.Color.FromArgb(0xE4, 0, 0, 0);
        bar.ButtonHoverForegroundColor = bar.ButtonForegroundColor;
        bar.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(0x0F, 255, 255, 255) : Windows.UI.Color.FromArgb(0x09, 0, 0, 0);
        bar.ButtonPressedBackgroundColor = dark ? Windows.UI.Color.FromArgb(0x0A, 255, 255, 255) : Windows.UI.Color.FromArgb(0x06, 0, 0, 0);
        bar.ButtonPressedForegroundColor = bar.ButtonForegroundColor;
        bar.ButtonInactiveForegroundColor = dark ? Windows.UI.Color.FromArgb(0x5D, 255, 255, 255) : Windows.UI.Color.FromArgb(0x5C, 0, 0, 0);
    }

    private void ApplyMaterial()
    {
        WindowMaterial.Apply(this, _settings);
        bool tinted = !ThemeLookup.HighContrast;
        Aurora.Visibility = tinted && _settings.Tint == "aurora" ? Visibility.Visible : Visibility.Collapsed;
        AccentTint.Visibility = tinted && _settings.Tint == "accent" ? Visibility.Visible : Visibility.Collapsed;
        Render();
    }

    private void OnSettingChanged(string key)
    {
        switch (key)
        {
            case "theme": ApplyTheme(); break;
            case "material" or "tint": ApplyMaterial(); break;
            case "sidebar": ApplySidebarLayout(animate: true); break;
            case "panes": ApplyPaneStyle(); break;
        }
        Render();
    }

    /// <summary>Comfortable: glass cards 8 epx apart. Compact: one frame of tiles with hairline gutters.</summary>
    private void ApplyPaneStyle() =>
        PaneGeometry.Gap = _settings.PaneStyle == "compact" ? PaneGeometry.CompactGap : PaneGeometry.CardGap;

    // ------------------------------------------------------------------ model

    private HostModel? CurrentHost => _model.Host(_model.Current?.Host);
    private SessionModel? CurrentSession => _model.CurrentSession;
    private WindowModel? CurrentWindow => CurrentSession is { } s ? s.Windows.FirstOrDefault(w => w.Id == s.ActiveWindow) ?? s.Windows.FirstOrDefault() : null;
    private ulong? FocusedPane => CurrentWindow is { FocusedPane: > 0 } w ? w.FocusedPane : CurrentWindow?.Panes.FirstOrDefault()?.Pane;

    private void OnModel(RexModel model)
    {
        var previousWindow = CurrentWindow;
        var previousFocus = FocusedPane;
        _model = model;
        if (_chosen is not null && _model.Current == _chosen) _chosenShown = true;
        if (_focusRequest is { } request && FocusedPane == request.Pane) _focusRequest = null;
        TryRestoreSession();
        SyncAppFocus();
        RememberSession();
        TrackActivity();
        Render();

        // Keyboard focus follows the server's focused pane when it changes under us; the first
        // pane shown takes focus from whatever the window focused on activation. A request waits
        // until the pane's card is actually on screen (a session's layout can arrive after it).
        bool moved = FocusedPane != previousFocus || CurrentWindow?.Id != previousWindow?.Id;
        if (FocusedPane is { } pane && PaneArea.IsShown(pane) && (!_paneFocusedOnce || _refocusPane || moved && FocusIsInPaneAreaOrNowhere()))
        {
            _paneFocusedOnce = true;
            _refocusPane = false;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!PaneArea.FocusContent(pane)) _refocusPane = true;
            });
        }
        if (_windowActive && CurrentSession is { } s && CurrentWindow is { } w && w.Id != previousWindow?.Id)
            App.Notifications.Withdraw(s.Id, w.Id);
    }

    // ------------------------------------------------------------------ chosen session and app focus

    /// <summary>
    /// Reopen the last (or demo) session once its saved host has connected, however long that takes
    /// (Retry, the network or Tailscale coming up later); never creates one. Anything the user opens
    /// first wins. Meanwhile the window may show the session the core picked, without claiming it.
    /// </summary>
    private void TryRestoreSession()
    {
        if (!_restorePending) return;
        if (PendingRestore is not { } target || _hosts.Find(target.Host) is null) { _restorePending = false; return; }
        if (_model.Host(target.Host) is not { IsConnected: true } host) return;
        _restorePending = false;
        // A session that is gone stays gone: show what the core picked until the user chooses.
        var session = host.Sessions.FirstOrDefault(x => x.Id == target.Session || x.Label == target.Session);
        if (session is not null) _ = OpenSessionAsync(host.Id, session.Id, quiet: true);
    }

    /// <summary>The host and session (label) restore is still waiting to reopen.</summary>
    private (string Host, string Session)? PendingRestore =>
        !_restorePending ? null
        : _hosts.IsDemo ? MockCore.DemoStartSession
        : _settings.RestoreLastSession && _settings.LastHost is { } h && _settings.LastSession is { } s ? (h, s) : null;

    /// <summary>The user opens a session (or restore reopens it): it is what the window may claim.</summary>
    private void Choose(CurrentRef session)
    {
        _restorePending = false;
        _chosen = session;
        _chosenShown = _model.Current == session;
        SyncAppFocus();
    }

    /// <summary>The user works in what the window shows (a click, a key, a pane or tab action): it becomes chosen.</summary>
    private void AdoptCurrent()
    {
        if (_model.Current is not { } current || current == _chosen) return;
        // A session the user just opened that has not arrived yet keeps priority over the stale one.
        if (_chosen is { } pending && !_chosenShown && _model.Host(pending.Host)?.Sessions.Any(s => s.Id == pending.Session) == true) return;
        Choose(current);
        // The core claims a session only once it is opened by the UI (not its own automatic pick).
        _ = _core.CommandAsync("open_session", new { host = current.Host, session = current.Session });
    }

    /// <summary>
    /// Tell the core whether the window is in front (app_focus). Gaining it claims the visible panes
    /// (resizing them to this window), so it is reported only while the chosen session is on screen.
    /// </summary>
    private void SyncAppFocus()
    {
        bool focused = _windowActive && _chosen is not null && _model.Current == _chosen;
        if (_appFocusSent == focused) return;
        _appFocusSent = focused;
        _ = _core.CommandAsync("app_focus", new { focused });
    }

    /// <summary>Remember the chosen session for next launch (never a session the core picked on its own).</summary>
    private void RememberSession()
    {
        var current = _model.Current;
        if (current is null || current != _chosen || current == _remembered) return;
        _remembered = current;
        if (_hosts.IsDemo) return;
        _settings.LastHost = current.Host;
        _settings.LastSession = CurrentSession?.Label ?? current.Session;
        _settings.Save();
    }

    private void Render()
    {
        var host = CurrentHost;
        var session = CurrentSession;
        var window = CurrentWindow;
        RenderTitleRow(host, session, window);
        SidebarView.Update(_model, _hosts, _tailnet, _settings.ColorfulIcons);
        RenderWorkspace(host, session, window);
        RenderHostInfoBar();
        RenderActivityBadge();
        RenderTaskbar(session);
        RenderSwitcherTip(session);
        AppWindow.Title = window is not null && session is not null && host is not null
            ? $"{TabLabel(window)} – {session.Label} · {host.Name} – Windex"
            : host is not null ? $"Windex – {host.Name}" : "Windex";
    }

    private static string TabLabel(WindowModel w) => string.IsNullOrWhiteSpace(w.Title) ? w.Label : w.Title;

    private void RenderWorkspace(HostModel? host, SessionModel? session, WindowModel? window)
    {
        bool noHosts = _hosts.Hosts.Count == 0 && _model.Hosts.Count == 0;
        FirstRunCard.Visibility = noHosts ? Visibility.Visible : Visibility.Collapsed;
        RenderAddHostTip();
        if (host is null || session is null)
        {
            PaneArea.Clear();
            ShowEmptyWorkspace(noHosts ? null
                : RestoreHost is { } waiting && PendingRestore is { } restore
                    ? ("\uE895", $"{restore.Session} opens when {waiting.Name} connects",
                        waiting.State is "connecting" or "reconnecting" ? $"Connecting to {waiting.Name}\u2026" : "Retry the host, or pick another session in the sidebar.", null)
                    : ("\uE8A9", "No session open", "Pick a session in the sidebar, or press Ctrl+Shift+K to switch.", "Switch session"));
            return;
        }
        if (session.Windows.Count == 0)
        {
            PaneArea.Clear();
            // Until the session is attached its tabs are simply not known yet: never offer to add one.
            ShowEmptyWorkspace(session.Attached
                ? ("\uE8A9", $"No tabs in {session.Label}", "Start a shell in this session.", "New tab")
                : ("\uE895", $"Opening {session.Label}…", $"Waiting for {host.Name} to send the session's tabs.", null));
            return;
        }
        ShowEmptyWorkspace(null);
        PaneArea.Show(host.Id, session, window, new PaneAreaOptions(
            WindowActive: _windowActive,
            DimMode: ThemeLookup.HighContrast ? "off" : _settings.DimInactive,
            Highlight: _settings.HighlightFocused,
            ShowHeaders: _settings.PaneHeaders != "hidden",
            Animate: Motion.Enabled));
    }

    /// <summary>
    /// First run: a TeachingTip at the sidebar's Add host button says where hosts come from. It
    /// shows once (dismissing it, or adding a host, retires it) and waits for the docked sidebar.
    /// </summary>
    private void RenderAddHostTip()
    {
        bool noHosts = _hosts.Hosts.Count == 0 && _model.Hosts.Count == 0;
        if (!noHosts || _settings.SeenAddHostTip)
        {
            if (AddHostTip.IsOpen) AddHostTip.IsOpen = false;
            return;
        }
        bool sidebarShown = !SidebarIsOverlay && _settings.SidebarOpen && SidebarView.AddHostTarget.ActualWidth > 0;
        if (sidebarShown == AddHostTip.IsOpen) return;
        // A sidebar that is hidden (or not laid out yet) puts the tip away until it is back.
        AddHostTip.Target = SidebarView.AddHostTarget;
        AddHostTip.IsOpen = sidebarShown;
    }

    private void AddHostTip_ActionButtonClick(TeachingTip sender, object args)
    {
        RetireAddHostTip();
        sender.IsOpen = false;
        ShowAddHost(null);
    }

    /// <summary>Got it (or Esc) retires the tip; closing it because the sidebar went away does not.</summary>
    private void AddHostTip_Closed(TeachingTip sender, TeachingTipClosedEventArgs args)
    {
        if (args.Reason != TeachingTipCloseReason.Programmatic) RetireAddHostTip();
    }

    private void RetireAddHostTip()
    {
        _settings.SeenAddHostTip = true;
        _settings.Save();
    }

    /// <summary>
    /// The first time a session of the user's own hosts is on screen (never in the demo), the
    /// identity chip is introduced as the switcher, with the Ctrl+K opt-in as its action.
    /// </summary>
    private void RenderSwitcherTip(SessionModel? session)
    {
        if (_settings.SeenSwitcherTip || _hosts.IsDemo || session is null || SwitcherTip.IsOpen || IdentityChip.ActualWidth <= 0) return;
        SwitcherTip.IsOpen = true;
    }

    private void SwitcherTip_ActionButtonClick(TeachingTip sender, object args)
    {
        sender.IsOpen = false;
        _settings.Update("shortcuts", s => { s.CtrlKSwitcher = true; s.SeenSwitcherTip = true; });
    }

    private void SwitcherTip_Closed(TeachingTip sender, TeachingTipClosedEventArgs args)
    {
        _settings.SeenSwitcherTip = true;
        _settings.Save();
    }

    private void ShowEmptyWorkspace((string Glyph, string Title, string Text, string? Button)? state)
    {
        EmptyWorkspace.Visibility = state is null ? Visibility.Collapsed : Visibility.Visible;
        if (state is not { } s) return;
        EmptyWorkspaceGlyph.Glyph = s.Glyph;
        EmptyWorkspaceTitle.Text = s.Title;
        EmptyWorkspaceText.Text = s.Text;
        EmptyWorkspaceButton.Content = s.Button;
        EmptyWorkspaceButton.Visibility = s.Button is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void EmptyWorkspaceButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentSession is { Windows.Count: 0 }) _ = NewTabAsync();
        else ShowPalette(PaletteMode.Switcher, anchored: false);
    }

    private bool FocusIsInPaneAreaOrNowhere()
    {
        if (Content.XamlRoot is null) return false;
        var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Content.XamlRoot) as DependencyObject;
        if (focused is null || focused == Root) return true;
        for (var n = focused; n is not null; n = VisualTreeHelper.GetParent(n))
        {
            if (n == PaneArea) return true;
            if (n == TitleRow || n == SidebarHost || n == InfoBars) return false;
        }
        return false;
    }

    // ------------------------------------------------------------------ host trouble (§9.3)

    /// <summary>The saved host a pending restore waits for: the host "on screen" while nothing is open.</summary>
    private HostModel? RestoreHost => PendingRestore is { } restore ? _model.Host(restore.Host) : null;

    private void RenderHostInfoBar()
    {
        var host = _model.Host(_troubleHost) ?? CurrentHost ?? RestoreHost;
        if (host is null) { HostInfoBar.IsOpen = false; return; }
        if (host.State == "reconnecting")
        {
            HostInfoBar.Severity = InfoBarSeverity.Warning;
            HostInfoBar.Title = $"Reconnecting to {host.Name}…";
            HostInfoBar.Message = host.Error ?? "";
            HostInfoBar.IsClosable = false;
            HostInfoBar.ActionButton = null;
            HostInfoBar.Content = new ProgressRing { Width = 16, Height = 16, MinWidth = 16, MinHeight = 16, IsActive = true, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
            HostInfoBar.IsOpen = true;
            return;
        }
        string key = host.Id + "|" + host.Error;
        if (host.State is "offline" or "error" && host.Error is not null && _dismissedTrouble != key)
        {
            var retry = new Button { Content = "Retry" };
            retry.Click += (_, _) => RetryHost(host.Id);
            var edit = new HyperlinkButton { Content = "Edit host…", Padding = new Thickness(0), Margin = new Thickness(0, 0, 0, 12) };
            edit.Click += (_, _) => _ = EditHostAsync(host.Id);
            HostInfoBar.Severity = InfoBarSeverity.Error;
            HostInfoBar.Title = $"Can't connect to {host.Name}";
            HostInfoBar.Message = host.Error;
            HostInfoBar.IsClosable = true;
            HostInfoBar.ActionButton = retry;
            HostInfoBar.Content = _hosts.Find(host.Id) is null ? null : edit;
            HostInfoBar.IsOpen = true;
            return;
        }
        HostInfoBar.IsOpen = false;
    }

    private void HostInfoBar_CloseButtonClick(InfoBar sender, object args)
    {
        var host = _model.Host(_troubleHost) ?? CurrentHost ?? RestoreHost;
        if (host is not null) _dismissedTrouble = host.Id + "|" + host.Error;
        _troubleHost = null;
    }

    // ------------------------------------------------------------------ in-app notices (§9.10)

    public void ShowNotice(string level, string title, string message)
    {
        var bar = new InfoBar
        {
            Title = title,
            Message = message,
            IsOpen = true,
            Severity = level switch { "error" => InfoBarSeverity.Error, "warning" => InfoBarSeverity.Warning, _ => InfoBarSeverity.Informational },
            Background = ThemeLookup.Brush(Root, "RexOverlayBrush"),
        };
        AutomationProperties.SetLiveSetting(bar, level == "error" ? Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive : Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        bar.Closed += (_, _) => Notices.Children.Remove(bar);
        Notices.Children.Add(bar);
        // At most three bars; a running transfer (and its Cancel) goes only when nothing else can.
        while (Notices.Children.Count > 3)
        {
            var others = Notices.Children.Where(c => !ReferenceEquals(c, bar)).ToList();
            Notices.Children.Remove(others.FirstOrDefault(c => _transferBars?.IsRunning(c) != true) ?? others[0]);
        }
        if (level != "error")
        {
            // Held until it fires: a timer only its own Tick handler refers to can be collected first.
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(6);
            timer.IsRepeating = false;
            timer.Tick += (_, _) => { Notices.Children.Remove(bar); _noticeTimers.Remove(timer); };
            _noticeTimers.Add(timer);
            timer.Start();
        }
    }

    // ------------------------------------------------------------------ desktop notifications (§9.10)

    private void OnDesktopNotification(DesktopNotify note)
    {
        bool wanted = note.Kind switch
        {
            "needs_input" => _settings.NotifyNeedsInput,
            "done" => _settings.NotifyDone,
            "bell" => _settings.NotifyBell,
            "error" => _settings.NotifyError,
            _ => _settings.NotifyProgramMessages,
        };
        var host = _model.Host(note.Host);
        var session = host?.Sessions.FirstOrDefault(s => s.Id == note.Session);
        var window = session?.Windows.FirstOrDefault(w => w.Id == note.Window)
                     ?? session?.Windows.FirstOrDefault(w => w.Panes.Any(p => p.Pane == note.Pane));
        bool visible = _windowActive && _model.Current?.Session == session?.Id && CurrentWindow?.Id == window?.Id && window is not null;
        if (note.Kind == "needs_input" && !_windowActive && _settings.FlashTaskbar) Native.FlashTaskbar(Hwnd);
        if (!wanted || visible || host is null || session is null) return;

        string tab = window is null ? session.Label : TabLabel(window);
        string title = note.Kind switch
        {
            "needs_input" => $"{tab} needs input",
            "done" => $"{tab} is done",
            "bell" => $"Bell in {tab}",
            "error" => $"{tab} hit an error",
            _ => string.IsNullOrWhiteSpace(note.Title) ? tab : note.Title,
        };
        string body = note.Kind is "osc9" or "osc777" ? note.Body : string.IsNullOrWhiteSpace(note.Body) ? (note.Kind == "done" ? "Finished" : note.Title) : note.Body;
        var pane = window?.Panes.FirstOrDefault(p => p.Pane == note.Pane) ?? window?.Panes.FirstOrDefault(p => p.Pane == window.FocusedPane);
        _ = App.Notifications.ShowAsync(title, body, $"{session.Label} · {host.Name}", pane?.Icon ?? "shell",
            new NotificationTarget(host.Id, session.Id, window?.Id, note.Pane), urgent: note.Kind is "needs_input" or "error");
    }

    /// <summary>A toast was clicked: bring the window forward and land on its pane.</summary>
    private void OpenTarget(NotificationTarget target)
    {
        AppWindow.Show();
        Native.SetForegroundWindow(Hwnd);
        _ = LandOnAsync(target.Host, target.Session, target.Window, target.Pane);
    }

    // ------------------------------------------------------------------ sidebar layout (§6)

    private void SetupSidebarGrip()
    {
        _sidebarGrip.Width = PaneSplitter.HitWidth;
        _sidebarGrip.Horizontal = true;
        _sidebarGrip.PositionReference = Body;
        _sidebarGrip.Dragged += (_, at) =>
        {
            double width = _settings.SidebarPosition == "right" ? Body.ActualWidth - at.X : at.X;
            _settings.SidebarWidth = Math.Clamp(width, 200, 360);
            ApplySidebarLayout(animate: false);
        };
        _sidebarGrip.DragCompleted += (_, _) => _settings.Save();
        _sidebarGrip.Equalize += (_, _) => { _settings.SidebarWidth = 260; _settings.Save(); ApplySidebarLayout(animate: false); };
        AutomationProperties.SetName(_sidebarGrip, "Resize sidebar");
        SidebarHost.Children.Add(_sidebarGrip);
    }

    /// <summary>Below 1000 epx (a half-screen snap at 100%) the panes keep the width and the sidebar floats.</summary>
    private bool SidebarIsOverlay => Root.ActualWidth > 0 && Root.ActualWidth < 1000;

    /// <summary>Docked: the pane area takes its final width at once; only the sidebar's visual slides.</summary>
    private void ApplySidebarLayout(bool animate)
    {
        bool right = _settings.SidebarPosition == "right";
        bool overlay = SidebarIsOverlay;
        bool open = overlay ? _overlayOpen : _settings.SidebarOpen;
        double width = _settings.SidebarWidth;
        bool motion = animate && Motion.Enabled;

        SidebarToggle.Content = right ? "\uE90D" : "\uE90C";
        _sidebarGrip.HorizontalAlignment = right ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        _sidebarGrip.Margin = right ? new Thickness(-5, 0, 0, 0) : new Thickness(0, 0, -5, 0);
        _sidebarGrip.Visibility = overlay ? Visibility.Collapsed : Visibility.Visible;

        if (overlay)
        {
            LeftColumn.Width = new GridLength(0);
            RightColumn.Width = new GridLength(0);
            Grid.SetColumn(SidebarHost, 0);
            Grid.SetColumnSpan(SidebarHost, 3);
            SidebarHost.Width = width;
            SidebarHost.HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            SidebarHost.Background = ThemeLookup.Brush(Root, "RexOverlayBrush");
            SidebarHost.BorderBrush = ThemeLookup.Brush(Root, "RexPaneStrokeBrush");
            SidebarHost.BorderThickness = right ? new Thickness(1, 0, 0, 0) : new Thickness(0, 0, 1, 0);
            SidebarHost.CornerRadius = right ? new CornerRadius(12, 0, 0, 12) : new CornerRadius(0, 12, 12, 0);
            Canvas.SetZIndex(SidebarHost, 1); // above the workspace
            // Lifted over the cards like a flyout (design §6.6), so it never reads as part of a pane.
            // An in-tree ThemeShadow only draws on its receivers: the workspace beside it.
            if (SidebarHost.Shadow is null)
            {
                var shadow = new ThemeShadow();
                shadow.Receivers.Add(Workspace);
                SidebarHost.Shadow = shadow;
            }
            SidebarHost.Translation = new Vector3(0, 0, 32);
        }
        else
        {
            Grid.SetColumn(SidebarHost, right ? 2 : 0);
            Grid.SetColumnSpan(SidebarHost, 1);
            (right ? RightColumn : LeftColumn).Width = new GridLength(open ? width : 0);
            (right ? LeftColumn : RightColumn).Width = new GridLength(0);
            SidebarHost.Width = double.NaN;
            SidebarHost.HorizontalAlignment = HorizontalAlignment.Stretch;
            SidebarHost.Background = null;
            SidebarHost.BorderThickness = new Thickness(0);
            SidebarHost.CornerRadius = new CornerRadius(0);
            Canvas.SetZIndex(SidebarHost, 0);
            SidebarHost.Translation = Vector3.Zero;
        }

        float from = right ? 16 : -16;
        var duration = Motion.Duration(open ? Motion.Normal : Motion.Fast);
        SidebarView.OpacityTransition = motion ? new ScalarTransition { Duration = duration } : null;
        SidebarView.TranslationTransition = motion ? new Vector3Transition { Duration = duration } : null;
        if (open)
        {
            SidebarHost.Visibility = Visibility.Visible;
            if (motion && SidebarView.Opacity < 1) SidebarView.Translation = new Vector3(from, 0, 0);
            SidebarView.Opacity = 1;
            SidebarView.Translation = Vector3.Zero;
        }
        else
        {
            SidebarView.Opacity = 0;
            SidebarView.Translation = new Vector3(from, 0, 0);
            if (motion && !overlay)
            {
                // Keep the column until the slide finishes, then let the panes resize once. The
                // timer is a field: one only a lambda refers to can be collected before it fires.
                (right ? RightColumn : LeftColumn).Width = new GridLength(width);
                if (_sidebarCollapse is null)
                {
                    _sidebarCollapse = DispatcherQueue.CreateTimer();
                    _sidebarCollapse.IsRepeating = false;
                    _sidebarCollapse.Tick += (_, _) =>
                    {
                        if (_settings.SidebarOpen || SidebarIsOverlay) return;
                        LeftColumn.Width = RightColumn.Width = new GridLength(0);
                        SidebarHost.Visibility = Visibility.Collapsed;
                    };
                }
                _sidebarCollapse.Interval = duration + TimeSpan.FromMilliseconds(3);
                _sidebarCollapse.Stop();
                _sidebarCollapse.Start();
            }
            else SidebarHost.Visibility = Visibility.Collapsed;
        }
        RenderAddHostTip();
    }

    public void ToggleSidebar()
    {
        if (SidebarIsOverlay) _overlayOpen = !_overlayOpen;
        else _settings.SidebarOpen = !_settings.SidebarOpen;
        _settings.Save();
        ApplySidebarLayout(animate: true);
    }
}
