// Find in terminal (design §5.7, §7.5). The find bar floats over the top-right of the body; every
// query change searches the pane on the core (rex_pane_search, on the pane's worker), and every
// frame brings back the matches on screen and the counts, which the renderer highlights and the
// bar shows. Find next walks up into older output (Enter, F3), Find previous back down.
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windex.Interop;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Windex.Terminal;

public sealed partial class TerminalView
{
    private readonly TerminalFindBar _findBar = new();
    /// <summary>The query and options the core is searching for (null: not searching).</summary>
    private (string Query, RexSearchFlags Flags)? _searchSent;
    /// <summary><see cref="Focus()"/> is moving keyboard focus into the terminal (out of the find bar too).</summary>
    private bool _focusingInput;

    /// <summary>The find bar is open.</summary>
    public bool IsFindOpen => _findBar.IsOpen;

    private void InitializeFind()
    {
        _findBar.CastShadowOn(_surfaceHost);
        _findBar.QueryChanged += (_, _) => RunSearch();
        _findBar.NextRequested += (_, _) => FindNext();
        _findBar.PreviousRequested += (_, _) => FindPrevious();
        _findBar.CloseRequested += (_, _) => CloseFind();
        _findBar.LosingFocus += OnFindBarLosingFocus;
    }

    /// <summary>
    /// The shell focuses a pane's content whenever it wants the pane to have the keyboard: on every
    /// press inside its card (handled ones too, so presses on the bar), when the palette closes, when
    /// the server's focused pane changes. With the open find bar holding the keyboard, that would
    /// send typing and Enter to the program, so the bar keeps it: only <see cref="Focus()"/> (Esc,
    /// Close) and a click in the terminal take it out. Redirected rather than cancelled, so the
    /// caller's focus request still succeeds.
    /// </summary>
    private void OnFindBarLosingFocus(UIElement sender, LosingFocusEventArgs args)
    {
        if (!_findBar.IsOpen || _focusingInput || args.FocusState != FocusState.Programmatic || !ReferenceEquals(args.NewFocusedElement, _input)) return;
        if (!args.TrySetNewFocusedElement(args.OldFocusedElement)) args.TryCancel();
    }

    /// <summary>
    /// Opens the find bar, or moves focus back into it, with its text selected so typing replaces
    /// it. A one-line selection in the terminal becomes the query.
    /// </summary>
    public void OpenFind()
    {
        if (Core is not { } core || Pane == 0) return;
        if (core.SelectionText(Pane) is { Length: > 0 and <= 512 } selected && selected.AsSpan().IndexOfAny('\r', '\n') < 0)
            _findBar.Query = selected;
        if (!_findBar.IsOpen)
        {
            _findBar.ResetCorner();
            _findBar.Fit(new Size(ActualWidth, ActualHeight));
            _findBar.Visibility = Visibility.Visible;
        }
        RunSearch();
        _findBar.FocusQuery();
        // A menu or the palette that opened the bar hands focus back to the terminal as it closes.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_findBar.IsOpen && !_findBar.HasFocus) _findBar.FocusQuery();
        });
    }

    /// <summary>Closes the find bar, clears the highlights and returns keyboard focus to the terminal.</summary>
    public void CloseFind() => CloseFind(Pane, refocus: true);

    private void CloseFind(ulong pane, bool refocus)
    {
        if (!_findBar.IsOpen) return;
        // Focus first: collapsing the bar while it has focus would hand focus to the next tab stop.
        if (refocus) Focus();
        _findBar.Visibility = Visibility.Collapsed;
        if (_searchSent is not null && pane != 0) Core?.Search(pane, null, RexSearchFlags.None);
        _searchSent = null;
    }

    /// <summary>A key event from inside the open find bar (the terminal leaves it alone).</summary>
    private bool FindBarHas(object source) => _findBar.IsOpen && _findBar.Contains(source as DependencyObject);

    /// <summary>The next match up, toward older output (wraps).</summary>
    public void FindNext() => StepFind(-1);

    /// <summary>The next match down, toward newer output (wraps).</summary>
    public void FindPrevious() => StepFind(+1);

    private void StepFind(int direction)
    {
        if (_searchSent is { Query.Length: > 0 } && Core is { } core && Pane != 0) core.SearchStep(Pane, direction);
    }

    private void RunSearch()
    {
        if (!_findBar.IsOpen || Core is not { } core || Pane == 0) return;
        var wanted = (_findBar.Query, _findBar.Flags);
        if (_searchSent == wanted) return;
        _searchSent = wanted;
        int result = core.Search(Pane, wanted.Query.Length == 0 ? null : wanted.Query, wanted.Flags);
        _findBar.SetInvalid(result == -3);
    }

    /// <summary>After each frame: the counts, and keeping the bar off the current match.</summary>
    private void UpdateFindBar()
    {
        if (!_findBar.IsOpen) return;
        _findBar.SetResults(_frame.SearchTotal, _frame.SearchCurrent, _frame.SearchBusy, _frame.SearchActive, _frame.SearchError);
        var (row0, col0, row1, col1) = _frame.CurrentMatch;
        if (row0 < 0 || _fonts is not { } fonts) return;
        var match = new Rect((_originX + col0 * fonts.CellWidth) / _scale, (_originY + row0 * fonts.CellHeight) / _scale,
            (col1 - col0 + 1) * fonts.CellWidth / _scale, (row1 - row0 + 1) * fonts.CellHeight / _scale);
        _findBar.AvoidMatch(match, new Size(ActualWidth, ActualHeight), SystemSettings.AnimationsEnabled);
    }

    // ------------------------------------------------------------------ match colours

    private static readonly UISettings SystemSettings = WatchSystemSettings();
    private sealed record Accents(Color Light2, Color Dark1);
    /// <summary>Read once; the accent changing (ColorValuesChanged, on a background thread) drops it.</summary>
    private static Accents? _accents;

    private static UISettings WatchSystemSettings()
    {
        var settings = new UISettings();
        settings.ColorValuesChanged += (_, _) => _accents = null;
        return settings;
    }

    /// <summary>Matches use the system accent as the chrome does: its Light 2 shade on a dark
    /// terminal, Dark 1 on a light one.</summary>
    private static Color MatchAccent(TerminalTheme theme)
    {
        var accents = _accents ??= new Accents(SystemSettings.GetColorValue(UIColorType.AccentLight2), SystemSettings.GetColorValue(UIColorType.AccentDark1));
        return theme.IsDark ? accents.Light2 : accents.Dark1;
    }

    private static Color WithAlpha(Color c, double alpha) => Color.FromArgb((byte)Math.Round(alpha * 255), c.R, c.G, c.B);

    /// <summary>The current match's text: black or white, whichever contrasts more with its fill
    /// (<paramref name="alpha"/> of the accent over the terminal background), as opaque
    /// premultiplied ARGB.</summary>
    private static uint CurrentMatchText(Color accent, double alpha, Color background)
    {
        double Linear(byte front, byte back)
        {
            double c = (alpha * front + (1 - alpha) * back) / 255;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        double luminance = 0.2126 * Linear(accent.R, background.R) + 0.7152 * Linear(accent.G, background.G) + 0.0722 * Linear(accent.B, background.B);
        // Black's contrast ratio, (L + 0.05) / 0.05, beats white's, 1.05 / (L + 0.05), above L = 0.179.
        return luminance > 0.179 ? 0xFF000000u : 0xFFFFFFFFu;
    }
}
