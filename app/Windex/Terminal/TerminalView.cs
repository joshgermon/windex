// TerminalView: one pane's terminal surface. It renders the pane's rex_frame (docs/contract.md
// "Frames") with Win2D and turns keyboard, IME, clipboard and pointer input into ICore calls.
//
// Surface: a premultiplied-alpha composition drawing surface (TerminalSurface) under the view's
// XAML chrome, so every unpainted pixel blends with whatever is behind it: the translucent,
// rounded pane card. (A CanvasSwapChainPanel showed the window ground through the card instead of
// the card; a CanvasControl or CanvasImageSource kept its GPU surface after the view was gone, see
// TerminalGraphics.) All drawing is in device pixels at the XAML root's rasterization scale, so
// cells land on whole pixels at any DPI. It draws only when invalidated, at most once per UI frame:
// by PaneDirty for its pane or a local change (blink, focus, hover, size, theme).
//
// Find in terminal (TerminalView.Find.cs): a find bar floats over the top-right of the body; the
// core searches on the pane's worker and each frame carries the matches on screen and the counts.
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windex.Core;
using Windex.Interop;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace Windex.Terminal;

public sealed partial class TerminalView : UserControl, IDisposable
{
    // ------------------------------------------------------------------ public surface

    public static readonly DependencyProperty CoreProperty = Register(nameof(Core), typeof(ICore), null, (v, _) => v.OnCoreChanged());
    public static readonly DependencyProperty PaneProperty = Register(nameof(Pane), typeof(ulong), 0UL, (v, e) => v.OnPaneChanged((ulong)e.OldValue));
    public static readonly DependencyProperty IsActiveProperty = Register(nameof(IsActive), typeof(bool), true, (v, _) => v.OnFocusLookChanged());
    public static readonly DependencyProperty TerminalFontFamilyProperty = Register(nameof(TerminalFontFamily), typeof(string), "Cascadia Mono", (v, _) => v.UpdateGeometry());
    public static readonly DependencyProperty TerminalFontSizeProperty = Register(nameof(TerminalFontSize), typeof(double), 11.0, (v, _) => v.UpdateGeometry());
    public static readonly DependencyProperty TerminalLineHeightProperty = Register(nameof(TerminalLineHeight), typeof(double), 1.25, (v, _) => v.UpdateGeometry());
    public static readonly DependencyProperty ThemeProperty = Register(nameof(Theme), typeof(TerminalTheme), null, (v, _) => v.OnThemeChanged());
    public static readonly DependencyProperty TransparentDefaultBackgroundProperty = Register(nameof(TransparentDefaultBackground), typeof(bool), true, (v, _) => v.Invalidate());

    /// <summary>The core the pane lives in.</summary>
    public ICore? Core { get => (ICore?)GetValue(CoreProperty); set => SetValue(CoreProperty, value); }
    /// <summary>The pane handle to show (0: none).</summary>
    public ulong Pane { get => (ulong)GetValue(PaneProperty); set => SetValue(PaneProperty, value); }
    /// <summary>Focused-pane styling. An inactive pane (or window) draws a hollow cursor.</summary>
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public string TerminalFontFamily { get => (string)GetValue(TerminalFontFamilyProperty); set => SetValue(TerminalFontFamilyProperty, value); }
    /// <summary>Font size in points (11 pt = 14.67 DIPs).</summary>
    public double TerminalFontSize { get => (double)GetValue(TerminalFontSizeProperty); set => SetValue(TerminalFontSizeProperty, value); }
    /// <summary>Cell height as a multiple of the font size (design §7.2: 1.25, range 1.0–2.0), rounded up to whole pixels.</summary>
    public double TerminalLineHeight { get => (double)GetValue(TerminalLineHeightProperty); set => SetValue(TerminalLineHeightProperty, value); }
    /// <summary>Cursor text, selection and composition colours; null follows the app theme (Rex Dusk / Rex Dawn).
    /// The rest of the theme reaches the terminal by being declared to it (<see cref="TerminalTheme.ToCoreTheme"/>).</summary>
    public TerminalTheme? Theme { get => (TerminalTheme?)GetValue(ThemeProperty); set => SetValue(ThemeProperty, value); }
    /// <summary>Leave cells with the terminal's default background unpainted so the card shows through.</summary>
    public bool TransparentDefaultBackground { get => (bool)GetValue(TransparentDefaultBackgroundProperty); set => SetValue(TransparentDefaultBackgroundProperty, value); }

    /// <summary>Copy the selection to the clipboard when a mouse selection ends.</summary>
    public bool CopyOnSelect { get; set; }

    /// <summary>Blink the cursor when the program asks for it (design §7.3 "Follow program"); false keeps it steady.</summary>
    public bool CursorBlinks
    {
        get => _cursorBlinks;
        set
        {
            _cursorBlinks = value;
            UpdateBlinkTimer();
            Invalidate();
        }
    }
    /// <summary>Right-click pastes (when the program is not tracking the mouse).</summary>
    public bool RightClickPastes { get; set; }

    /// <summary>The cursor shape while the program has not chosen one (DECSCUSR; design §7.3, default a filled block).</summary>
    public RexCursorShape DefaultCursorShape
    {
        get => _defaultCursor;
        set
        {
            if (_defaultCursor == value) return;
            _defaultCursor = value;
            _defaultCursorSent = false;
            SendDefaultCursor();
        }
    }
    /// <summary>App shortcuts: return true to consume a key before it reaches the terminal.</summary>
    public Func<KeyInput, bool>? ShortcutFilter { get; set; }

    /// <summary>The terminal took keyboard focus (click, Tab or <see cref="Focus()"/>).</summary>
    public event EventHandler? GotPaneFocus;
    /// <summary>The program changed the terminal title (OSC 0/2).</summary>
    public event EventHandler<string>? TitleChanged;
    /// <summary>Ctrl+click on a link. Unhandled http, https and mailto links open in the default app.</summary>
    public event EventHandler<TerminalLinkEventArgs>? LinkActivated;

    /// <summary>Timings and size of the last drawn frame.</summary>
    public TerminalRenderStats RenderStats => _renderer?.Stats ?? default;
    /// <summary>The grid this view asks for (columns, rows) at its current size and font.</summary>
    public (int Columns, int Rows) GridSize => (_cols, _rows);
    /// <summary>The current terminal title.</summary>
    public string Title => _renderer?.Title ?? "";

    /// <summary>Raised after every drawn frame (diagnostics and benchmarks).</summary>
    internal event EventHandler<TerminalRenderStats>? Rendered;

    // ------------------------------------------------------------------ state

    private readonly Grid _root = new() { Background = new SolidColorBrush(Colors.Transparent) };
    private readonly Border _surfaceHost = new();
    private readonly TerminalSurface _surface;
    private readonly Canvas _imeLayer = new() { IsHitTestVisible = false };
    private readonly TerminalScrollBar _scrollBar = new();
    private readonly FrameworkElement _pausedStrip;
    private readonly DispatcherQueueTimer _resizeTimer;
    private readonly DispatcherQueueTimer _blinkTimer;

    private CanvasDevice? _device;
    private TerminalRenderer? _renderer;
    private TerminalFonts? _fonts;
    private ICore? _subscribedCore;
    private XamlRoot? _xamlRoot;
    private InputFocusController? _islandFocus;
    private FrameState _frame;

    private float _scale = 1;
    private int _surfaceWidth, _surfaceHeight, _originX, _originY, _cols, _rows;
    private (int Cols, int Rows, int CellWidth, int CellHeight) _sentGrid, _wantedGrid;
    private bool _hasFocus, _windowFocused = true, _blinkVisible = true, _cursorBlinks = true;
    private RexCursorShape _defaultCursor = RexCursorShape.Block;
    /// <summary>The core has <see cref="_defaultCursor"/> for the current pane.</summary>
    private bool _defaultCursorSent;
    /// <summary>In the tree and hooked up (Loaded); <see cref="_disposed"/>: never again.</summary>
    private bool _active, _disposed;

    public TerminalView()
    {
        IsTabStop = false;
        Padding = new Thickness(10, 2, 10, 6);
        AutomationProperties.SetName(this, "Terminal");

        _input = CreateInputSink();
        _pausedStrip = CreatePausedStrip();
        _surface = new TerminalSurface(_surfaceHost);
        _imeLayer.Children.Add(_input);
        _root.Children.Add(_surfaceHost);
        _root.Children.Add(_imeLayer);
        _root.Children.Add(_scrollBar);
        _root.Children.Add(_pausedStrip);
        _root.Children.Add(_findBar);
        Content = _root;

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _resizeTimer = dispatcher.CreateTimer();
        _resizeTimer.Interval = TimeSpan.FromMilliseconds(30);
        _blinkTimer = dispatcher.CreateTimer();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => UpdateGeometry();
        ActualThemeChanged += (_, _) => OnThemeChanged();
        RegisterPropertyChangedCallback(PaddingProperty, (_, _) => UpdateGeometry());
        _scrollBar.ScrollToRow += (_, row) => Core?.Scroll(Pane, RexScrollOp.Row, row);
        _scrollBar.ScrollPages += (_, pages) => Core?.Scroll(Pane, RexScrollOp.Delta, pages * Math.Max(1, _frame.Rows - 1));

        InitializeInput();
        InitializePointer();
        InitializeFind();
    }

    private static DependencyProperty Register(string name, Type type, object? defaultValue, Action<TerminalView, DependencyPropertyChangedEventArgs> changed) =>
        DependencyProperty.Register(name, type, typeof(TerminalView), new PropertyMetadata(defaultValue, (d, e) => changed((TerminalView)d, e)));

    // ------------------------------------------------------------------ commands

    /// <summary>Moves keyboard focus into the terminal, out of the find bar too.</summary>
    public bool Focus()
    {
        _focusingInput = true;
        try
        {
            return _input.Focus(FocusState.Programmatic);
        }
        finally
        {
            _focusingInput = false;
        }
    }

    /// <summary>Copies the selection to the clipboard. Returns false when nothing is selected.</summary>
    public bool Copy()
    {
        var text = Core?.SelectionText(Pane);
        if (string.IsNullOrEmpty(text)) return false;
        var package = new DataPackage();
        package.SetText(text);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Clipboard.SetContent(package);
                break;
            }
            catch (COMException) when (attempt < 4)
            {
                Thread.Sleep(15); // Another process (clipboard history, a reader) holds it open; it lets go quickly.
            }
            catch (COMException)
            {
                return false;
            }
        }
        try
        {
            Clipboard.Flush(); // Keep the text on the clipboard after this app exits.
        }
        catch (COMException)
        {
            // The text is on the clipboard; only its survival past exit is lost.
        }
        return true;
    }

    /// <summary>Pastes the clipboard (bracketed when the program asked for it). Files or a picture,
    /// and multi-line text for a program without bracketed paste, go to <see cref="PasteHandler"/> first.</summary>
    public void Paste() => _ = PasteAsync();

    /// <summary>Pastes text dropped on the terminal, with the same checks as a paste from the clipboard.</summary>
    public void PasteText(string text) => _ = PasteTextAsync(Pane, text);

    /// <summary>Decides the pastes plain text can't cover; null pastes text as it is.</summary>
    public IPasteHandler? PasteHandler { get; set; }

    private async Task PasteAsync()
    {
        ulong pane = Pane;
        if (pane == 0) return;
        string text;
        try
        {
            var content = Clipboard.GetContent();
            if (PasteHandler is { } handler && await handler.PasteDataAsync(pane, content)) return;
            if (!content.Contains(StandardDataFormats.Text)) return;
            text = await content.GetTextAsync();
        }
        catch (COMException)
        {
            return;
        }
        await PasteTextAsync(pane, text);
    }

    private async Task PasteTextAsync(ulong pane, string text)
    {
        if (pane == 0 || string.IsNullOrEmpty(text)) return;
        if (PasteHandler is { } guard && text.AsSpan().IndexOfAny('\r', '\n') >= 0 && !ProgramTakesBracketedPaste()
            && !await guard.ConfirmMultilinePasteAsync(pane, text)) return;
        if (Core is not { } core || Pane != pane) return;
        // Line breaks are carriage returns on a terminal, as Enter sends.
        core.Paste(pane, text.Replace("\r\n", "\r").Replace('\n', '\r'));
        ScrollToBottomIfScrolled();
    }

    /// <summary>The program asked for bracketed paste (mode 2004): it sees a paste as one block, not as typed lines.</summary>
    private unsafe bool ProgramTakesBracketedPaste()
    {
        if (Core is not { } core || Pane == 0) return false;
        RexFrame* frame = core.LockFrame(Pane);
        if (frame is null) return false;
        try { return frame->BracketedPaste != 0; }
        finally { core.UnlockFrame(Pane); }
    }

    public void SelectAll() => Core?.Select(Pane, RexSelectOp.All, 0, 0, 0);

    /// <summary>The view is being discarded: stop listening to the core and release the GPU surface
    /// now rather than when it leaves the tree.</summary>
    public void Dispose()
    {
        _disposed = true;
        CloseFind(Pane, refocus: false);
        Deactivate();
    }

    public void ScrollToBottom() => Core?.Scroll(Pane, RexScrollOp.Bottom, 0);

    private void ScrollToBottomIfScrolled()
    {
        if (_frame.Valid && _frame.ViewportTop + (ulong)_frame.Rows < _frame.TotalRows) ScrollToBottom();
    }

    // ------------------------------------------------------------------ lifecycle

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        Activate();
        UpdateGeometry();
        Invalidate();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Moved within one tick, the view can be loaded again before this arrives.
        if (!IsLoaded) Deactivate();
    }

    /// <summary>
    /// Hooks the view up to the core, the window and the GPU while it is in the tree. Everything
    /// here is undone by <see cref="Deactivate"/>: a handler left on a timer, the island's focus
    /// controller or the XAML root would keep the whole view alive (none of them is part of XAML's
    /// reference tracking), and so would the input box's (see HookInputSink), so an unloaded view
    /// is collectable even if nobody disposes it.
    /// </summary>
    private void Activate()
    {
        if (_active) return;
        _active = true;
        Subscribe(Core);
        HookInputSink(true);
        _resizeTimer.Tick += OnResizeTimer;
        _blinkTimer.Tick += OnBlinkTimer;
        TerminalGraphics.Attach(this);
        _device = TerminalGraphics.Device;
        if (XamlRoot is { } root)
        {
            _xamlRoot = root;
            root.Changed += OnXamlRootChanged;
            if (root.ContentIsland is { } island)
            {
                _islandFocus = InputFocusController.GetForIsland(island);
                _islandFocus.GotFocus += OnIslandFocusChanged;
                _islandFocus.LostFocus += OnIslandFocusChanged;
                _windowFocused = _islandFocus.HasFocus;
            }
        }
    }

    /// <summary>Out of the tree (or disposed): unhooked, and the GPU surface and resources released.</summary>
    private void Deactivate()
    {
        if (!_active) return;
        _active = false;
        if (_hasFocus) SetKeyboardFocus(false);
        Subscribe(null);
        HookInputSink(false);
        _resizeTimer.Stop();
        _resizeTimer.Tick -= OnResizeTimer;
        _blinkTimer.Stop();
        _blinkTimer.Tick -= OnBlinkTimer;
        if (_islandFocus is not null)
        {
            _islandFocus.GotFocus -= OnIslandFocusChanged;
            _islandFocus.LostFocus -= OnIslandFocusChanged;
            _islandFocus = null;
        }
        if (_xamlRoot is not null)
        {
            _xamlRoot.Changed -= OnXamlRootChanged;
            _xamlRoot = null;
        }
        TerminalGraphics.Detach(this);
        _surface.Dispose();
        _renderer?.Dispose();
        _renderer = null;
        _device = null;
    }

    /// <summary>The GPU device was lost and replaced: everything drawn with the old one is rebuilt.</summary>
    internal void OnDeviceReplaced()
    {
        _renderer?.Dispose();
        _renderer = null;
        _device = TerminalGraphics.Device;
        UpdateGeometry();
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (sender.RasterizationScale != _scale) UpdateGeometry();
    }

    private void OnIslandFocusChanged(InputFocusController sender, object args)
    {
        _windowFocused = sender.HasFocus;
        OnFocusLookChanged();
    }

    private void Subscribe(ICore? core)
    {
        if (_subscribedCore == core) return;
        if (_subscribedCore is not null) _subscribedCore.PaneDirty -= OnPaneDirty;
        _subscribedCore = core;
        if (core is not null) core.PaneDirty += OnPaneDirty;
    }

    private void OnCoreChanged()
    {
        // The search ran on the old core, which is going away with it.
        _searchSent = null;
        _findBar.Visibility = Visibility.Collapsed;
        if (_active) Subscribe(Core);
        ResetPaneState();
    }

    private void OnPaneChanged(ulong old)
    {
        if (old != 0 && _hasFocus) _subscribedCore?.Focus(old, false);
        CloseFind(old, refocus: false);
        ResetPaneState();
        if (_hasFocus && Pane != 0) Core?.Focus(Pane, true);
    }

    private void ResetPaneState()
    {
        _frame = default;
        _sentGrid = default;
        _defaultCursorSent = false;
        SendDefaultCursor();
        _renderer?.Reset();
        ClearHover();
        UpdateGeometry();
        Invalidate();
    }

    private void OnPaneDirty(object? sender, ulong pane)
    {
        if (pane == Pane) Invalidate();
    }

    private void OnThemeChanged()
    {
        var theme = EffectiveTheme;
        _input.Foreground = new SolidColorBrush(theme.Foreground);
        _input.Background = new SolidColorBrush(theme.Background);
        if (!_frame.Valid) _scrollBar.SetColor(theme.Foreground);
        Invalidate();
    }

    private TerminalTheme EffectiveTheme => Theme ?? TerminalTheme.ForAppTheme(ActualTheme == ElementTheme.Dark);

    private void OnFocusLookChanged()
    {
        _blinkVisible = true;
        Invalidate();
    }

    // ------------------------------------------------------------------ geometry

    /// <summary>Re-measures cells and the grid in device pixels and asks the core for the new size.</summary>
    private void UpdateGeometry()
    {
        if (!_active || _device is null || XamlRoot is null) return;
        float scale = (float)XamlRoot.RasterizationScale;
        int width = (int)Math.Round(ActualWidth * scale), height = (int)Math.Round(ActualHeight * scale);
        if (width <= 0 || height <= 0) return;

        _scale = scale;
        _fonts = TerminalFonts.Get(_device, TerminalFontFamily, (float)(TerminalFontSize * 96.0 / 72.0), (float)Math.Clamp(TerminalLineHeight, 1.0, 2.0), scale);
        var pad = Padding;
        _originX = (int)Math.Round(pad.Left * scale);
        _originY = (int)Math.Round(pad.Top * scale);
        int right = (int)Math.Round(pad.Right * scale), bottom = (int)Math.Round(pad.Bottom * scale);
        _cols = Math.Clamp((width - _originX - right) / _fonts.CellWidth, 1, ushort.MaxValue);
        _rows = Math.Clamp((height - _originY - bottom) / _fonts.CellHeight, 1, ushort.MaxValue);

        _surfaceWidth = width;
        _surfaceHeight = height;
        _surface.Resize(width, height, scale);
        if (_renderer is null) _renderer = new TerminalRenderer(_device, _fonts);
        else _renderer.Fonts = _fonts;

        _scrollBar.Margin = new Thickness(0, pad.Top, 2, pad.Bottom);
        _pausedStrip.Margin = new Thickness(pad.Left, 0, pad.Right, pad.Bottom + 4);
        _findBar.Fit(new Windows.Foundation.Size(ActualWidth, ActualHeight));
        UpdateInputSinkFont();
        RequestResize((_cols, _rows, _fonts.CellWidth, _fonts.CellHeight));
        Invalidate();
    }

    /// <summary>Resizes immediately, then at most every 30 ms while the size keeps changing (live resize).</summary>
    private void RequestResize((int, int, int, int) grid)
    {
        _wantedGrid = grid;
        if (_wantedGrid == _sentGrid || _resizeTimer.IsRunning) return;
        SendResize();
        _resizeTimer.Start();
    }

    private void OnResizeTimer(DispatcherQueueTimer timer, object args)
    {
        timer.Stop();
        if (_wantedGrid != _sentGrid && SendResize()) timer.Start();
    }

    private bool SendResize()
    {
        if (Core is not { } core || Pane == 0) return false;
        var (cols, rows, cw, ch) = _wantedGrid;
        if (core.Resize(Pane, (ushort)cols, (ushort)rows, (uint)cw, (uint)ch) < 0) return false;
        _sentGrid = _wantedGrid;
        return true;
    }

    // ------------------------------------------------------------------ rendering

    /// <summary>Schedules a redraw on the next UI frame (once, however often it is asked).</summary>
    private void Invalidate()
    {
        if (_renderer is not null) TerminalGraphics.RequestRender(this);
    }

    /// <summary>Draws the pane's current frame (TerminalGraphics calls it once per invalidated UI frame).
    /// A lost device surfaces as an exception here; TerminalGraphics then replaces it.</summary>
    internal unsafe void Render()
    {
        if (_renderer is null || _fonts is null || !_surface.HasSurface) return;
        var options = BuildRenderOptions();
        bool titleChanged = false;
        if (Core is { } core && Pane != 0)
        {
            var pane = Pane;
            RexFrame* frame = core.LockFrame(pane);
            if (frame is not null)
            {
                try { _frame = _renderer.Prepare(frame, options, out titleChanged); }
                finally { core.UnlockFrame(pane); }
                // A demo pane's terminal exists only once it has a size: retry until it takes.
                SendDefaultCursor();
            }
        }
        using (var ds = _surface.CreateDrawingSession())
            _renderer.Draw(ds, options, ToRgb(EffectiveTheme.Background));
        AfterRender(titleChanged);
        Rendered?.Invoke(this, _renderer.Stats);
    }

    private RenderOptions BuildRenderOptions()
    {
        var theme = EffectiveTheme;
        var accent = MatchAccent(theme);
        const double CurrentMatchAlpha = 0.80;
        bool focused = _hasFocus && _windowFocused && IsActive;
        var cursor = !focused ? CursorLook.Unfocused
            : _frame.CursorBlink && !_blinkVisible ? CursorLook.Hidden
            : CursorLook.Focused;
        return new RenderOptions
        {
            OriginX = _originX,
            OriginY = _originY,
            SurfaceWidth = _surfaceWidth,
            SurfaceHeight = _surfaceHeight,
            Cols = _cols,
            Rows = _rows,
            TransparentBackground = TransparentDefaultBackground,
            FillPadding = true,
            Cursor = cursor,
            CursorText = 0xFF000000u | ToRgb(theme.CursorText),
            Selection = theme.SelectionBackground,
            SelectionForeground = theme.SelectionForeground,
            LinkRow = _hover?.Row ?? -1,
            LinkStart = _hover?.Start ?? 0,
            LinkEnd = _hover?.End ?? 0,
            SelectionRadius = 3 * _scale,
            CursorBarWidth = Math.Max(1, (int)Math.Round(2 * _scale)),
            MatchFill = WithAlpha(accent, 0.18),
            MatchStroke = WithAlpha(accent, 0.70),
            CurrentMatchFill = WithAlpha(accent, CurrentMatchAlpha),
            CurrentMatchText = CurrentMatchText(accent, CurrentMatchAlpha, theme.Background),
            MatchRadius = 2 * _scale,
            MatchStrokeWidth = Math.Max(1, MathF.Round(_scale)),
        };
    }

    /// <summary>The cursor shape setting reaches the core once per pane (and again when it changes).</summary>
    private void SendDefaultCursor()
    {
        if (_defaultCursorSent || Core is not { } core || Pane == 0) return;
        _defaultCursorSent = core.SetDefaultCursor(Pane, _defaultCursor) >= 0;
    }

    private void AfterRender(bool titleChanged)
    {
        if (titleChanged) TitleChanged?.Invoke(this, _renderer!.Title);
        if (!_frame.Valid) return;
        _scrollBar.SetColor(ToColor(_frame.Fg));
        _scrollBar.Update(_frame.TotalRows, _frame.ViewportTop, _frame.Rows);
        _pausedStrip.Visibility = _frame.Paused ? Visibility.Visible : Visibility.Collapsed;
        PositionInputSink();
        UpdateBlinkTimer();
        UpdateFindBar();
    }

    private void OnBlinkTimer(DispatcherQueueTimer timer, object args)
    {
        _blinkVisible = !_blinkVisible;
        Invalidate();
    }

    private void UpdateBlinkTimer()
    {
        bool blink = _cursorBlinks && _frame.CursorBlink && _hasFocus && _windowFocused && IsActive && _frame.CursorX >= 0;
        uint period = KeyboardMap.GetCaretBlinkTime();
        if (!blink || period == 0 || period == uint.MaxValue)
        {
            _blinkTimer.Stop();
            _blinkVisible = true;
            return;
        }
        if (_blinkTimer.IsRunning) return;
        _blinkTimer.Interval = TimeSpan.FromMilliseconds(period);
        _blinkTimer.Start();
    }

    /// <summary>Typing shows the cursor steadily until the next blink period.</summary>
    private void ResetBlink()
    {
        if (!_blinkTimer.IsRunning) return;
        _blinkTimer.Stop();
        _blinkTimer.Start();
        if (!_blinkVisible) { _blinkVisible = true; Invalidate(); }
    }

    // ------------------------------------------------------------------ chrome

    private static FrameworkElement CreatePausedStrip()
    {
        const string xaml = """
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    VerticalAlignment="Bottom" HorizontalAlignment="Center" Visibility="Collapsed" IsHitTestVisible="False"
                    CornerRadius="4" Padding="10,5" BorderThickness="1"
                    Background="{ThemeResource SystemFillColorCautionBackgroundBrush}"
                    BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}">
                <StackPanel Orientation="Horizontal" Spacing="8">
                    <FontIcon Glyph="&#xE7BA;" FontSize="14" Foreground="{ThemeResource SystemFillColorCautionBrush}" />
                    <TextBlock Text="Input paused — reconnecting…" FontSize="12" VerticalAlignment="Center"
                               Foreground="{ThemeResource TextFillColorPrimaryBrush}" />
                </StackPanel>
            </Border>
            """;
        var strip = (FrameworkElement)XamlReader.Load(xaml);
        AutomationProperties.SetLiveSetting(strip, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        return strip;
    }

    protected override Microsoft.UI.Xaml.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new TerminalAutomationPeer(this);

    private sealed partial class TerminalAutomationPeer(TerminalView owner) : Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer(owner)
    {
        protected override Microsoft.UI.Xaml.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() =>
            Microsoft.UI.Xaml.Automation.Peers.AutomationControlType.Custom;

        protected override string GetLocalizedControlTypeCore() => "terminal";

        protected override string GetClassNameCore() => nameof(TerminalView);
    }

    private static uint ToRgb(Color c) => ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

    private static Color ToColor(uint rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}

/// <summary>What a terminal asks its app before a paste it can't make alone.</summary>
public interface IPasteHandler
{
    /// <summary>The clipboard holds files, or a picture and no text: take them (upload) and return true.</summary>
    Task<bool> PasteDataAsync(ulong pane, DataPackageView content);
    /// <summary>Text with line breaks for a program that has not asked for bracketed paste: true to paste it.</summary>
    Task<bool> ConfirmMultilinePasteAsync(ulong pane, string text);
}

/// <summary>A Ctrl+clicked link. Set <see cref="Handled"/> to stop the default (open http/https/mailto).</summary>
public sealed class TerminalLinkEventArgs(Uri uri) : EventArgs
{
    public Uri Uri { get; } = uri;
    public bool Handled { get; set; }
}
