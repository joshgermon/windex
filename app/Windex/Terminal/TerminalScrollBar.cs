// The scrollback indicator over a terminal's right edge (docs/design.md §7.4): a 2 epx panning
// indicator that appears while scrolling and fades after a second, widening into a draggable
// thumb when the pointer comes within 16 epx of the edge. It is chrome, drawn by XAML above the
// terminal surface, so fading it never redraws terminal content.
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Windex.Terminal;

internal sealed partial class TerminalScrollBar : Grid
{
    public const double HoverZone = 16;
    private const double ThinWidth = 2, WideWidth = 6, MinThumb = 24;

    private readonly Border _thumb;
    private readonly SolidColorBrush _thumbBrush = new(Colors.Gray);
    private readonly DispatcherQueueTimer _fadeTimer;
    private ulong _total, _top;
    private int _rows;
    private bool _expanded, _dragging;
    private double _dragStartY;
    private ulong _dragStartTop;

    /// <summary>Asks the terminal to show this absolute row at the top of the viewport.</summary>
    public event EventHandler<long>? ScrollToRow;
    /// <summary>Asks the terminal to move by this many pages (track clicks).</summary>
    public event EventHandler<int>? ScrollPages;

    public TerminalScrollBar()
    {
        Width = HoverZone - 4;
        HorizontalAlignment = HorizontalAlignment.Right;
        Background = new SolidColorBrush(Colors.Transparent);
        IsHitTestVisible = false;
        Opacity = 0;
        OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(167) };
        _thumb = new Border
        {
            Width = ThinWidth,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Background = _thumbBrush,
            CornerRadius = new CornerRadius(1),
        };
        Children.Add(_thumb);
        _fadeTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _fadeTimer.Interval = TimeSpan.FromSeconds(1);
        _fadeTimer.IsRepeating = false;
        // A timer is not part of XAML's reference tracking: its Tick handler would keep this element
        // (and the terminal around it) alive, so it is attached only while the element is loaded.
        Loaded += (_, _) => { _fadeTimer.Tick -= OnFadeTimer; _fadeTimer.Tick += OnFadeTimer; };
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
            _fadeTimer.Stop();
            _fadeTimer.Tick -= OnFadeTimer;
        };

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += (_, _) => EndDrag();
        SizeChanged += (_, _) => Layout();
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    }

    public bool HasScrollback => _total > (ulong)_rows && _rows > 0;

    private void OnFadeTimer(DispatcherQueueTimer timer, object args)
    {
        if (!_expanded && !_dragging) Opacity = 0;
    }

    public void SetColor(Color foreground) => _thumbBrush.Color = Color.FromArgb(0x80, foreground.R, foreground.G, foreground.B);

    /// <summary>New frame geometry. Returns true when the viewport moved.</summary>
    public bool Update(ulong totalRows, ulong viewportTop, int rows)
    {
        bool moved = viewportTop != _top && _rows > 0;
        _total = totalRows; _top = viewportTop; _rows = rows;
        if (!HasScrollback) { Opacity = 0; SetExpanded(false); }
        Layout();
        if (moved) Flash();
        return moved;
    }

    /// <summary>Shows the indicator for a second (after a scroll).</summary>
    public void Flash()
    {
        if (!HasScrollback) return;
        Opacity = 1;
        _fadeTimer.Stop();
        _fadeTimer.Start();
    }

    /// <summary>The pointer is (or is not) within the hover zone of the terminal's right edge.</summary>
    public void SetExpanded(bool expanded)
    {
        expanded &= HasScrollback;
        if (_expanded == expanded || _dragging) return;
        _expanded = expanded;
        IsHitTestVisible = expanded;
        _thumb.Width = expanded ? WideWidth : ThinWidth;
        _thumb.CornerRadius = new CornerRadius(expanded ? 3 : 1);
        if (expanded) { Opacity = 1; _fadeTimer.Stop(); }
        else Flash();
    }

    private (double Top, double Height) ThumbGeometry()
    {
        double track = ActualHeight;
        if (!HasScrollback || track <= 0) return (0, 0);
        double height = Math.Max(MinThumb, track * _rows / _total);
        double range = Math.Max(1, _total - (ulong)_rows);
        double top = (track - height) * Math.Min(1, _top / range);
        return (top, height);
    }

    private void Layout()
    {
        var (top, height) = ThumbGeometry();
        _thumb.Height = Math.Max(0, height);
        _thumb.Margin = new Thickness(0, top, 0, 0);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        var y = e.GetCurrentPoint(this).Position.Y;
        var (top, height) = ThumbGeometry();
        if (y < top) { ScrollPages?.Invoke(this, -1); return; }
        if (y > top + height) { ScrollPages?.Invoke(this, 1); return; }
        _dragging = true;
        _dragStartY = y;
        _dragStartTop = _top;
        CapturePointer(e.Pointer);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        if (!_dragging) return;
        var (_, height) = ThumbGeometry();
        double track = ActualHeight - height;
        if (track <= 0) return;
        double range = _total - (ulong)_rows;
        double rows = (e.GetCurrentPoint(this).Position.Y - _dragStartY) / track * range;
        long row = (long)Math.Clamp(_dragStartTop + rows, 0, range);
        ScrollToRow?.Invoke(this, row);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        ReleasePointerCapture(e.Pointer);
        EndDrag();
    }

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        Flash();
    }
}
