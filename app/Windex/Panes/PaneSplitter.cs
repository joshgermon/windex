// A divider over the gutter between two panes (design §5.3). The gutter itself shows the ground;
// this 12 epx hit area (centred on it) adds the resize cursor, a hover grip and the drag.
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windex.Shell;
using Windows.Foundation;

namespace Windex.Panes;

public sealed partial class PaneSplitter : Grid
{
    public const double HitWidth = 12;

    private readonly Border _grip = new() { CornerRadius = new CornerRadius(1.5), Opacity = 0, IsHitTestVisible = false };
    private readonly DispatcherTimer _hoverDelay = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private bool _dragging;
    private bool _horizontal;

    public string SplitId { get; }

    /// <summary>Pointer position in the coordinates of <see cref="PositionReference"/> while dragging.</summary>
    public event EventHandler<Point>? Dragged;
    public event EventHandler? DragCompleted;
    public event EventHandler? Equalize;

    /// <summary>The element drag positions are reported against (the layout panel).</summary>
    public UIElement? PositionReference { get; set; }

    public PaneSplitter(string splitId)
    {
        SplitId = splitId;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Children.Add(_grip);
        _grip.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(83) };
        _hoverDelay.Tick += (_, _) => { _hoverDelay.Stop(); ShowGrip(); };

        PointerEntered += (_, _) => _hoverDelay.Start();
        PointerExited += (_, _) => { _hoverDelay.Stop(); if (!_dragging) _grip.Opacity = 0; };
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += (_, e) => EndDrag(e.Pointer);
        PointerCaptureLost += (_, e) => EndDrag(null);
        DoubleTapped += (_, e) => { e.Handled = true; Equalize?.Invoke(this, EventArgs.Empty); };
        ActualThemeChanged += (_, _) => Paint();
        AutomationProperties.SetName(this, "Resize panes");
    }

    public bool Horizontal
    {
        get => _horizontal;
        set
        {
            _horizontal = value;
            ProtectedCursor = InputSystemCursor.Create(value ? InputSystemCursorShape.SizeWestEast : InputSystemCursorShape.SizeNorthSouth);
            Paint();
        }
    }

    public bool IsDragging => _dragging;

    private void ShowGrip() { Paint(); _grip.Opacity = 1; }

    private void Paint()
    {
        _grip.Background = ThemeLookup.Brush(this, _dragging ? "RexAccentBrush" : "RexSplitterGripBrush");
        if (_dragging)
        {
            _grip.Width = _horizontal ? 2 : double.NaN;
            _grip.Height = _horizontal ? double.NaN : 2;
            _grip.HorizontalAlignment = _horizontal ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
            _grip.VerticalAlignment = _horizontal ? VerticalAlignment.Stretch : VerticalAlignment.Center;
        }
        else
        {
            _grip.Width = _horizontal ? 3 : 32;
            _grip.Height = _horizontal ? 32 : 3;
            _grip.HorizontalAlignment = HorizontalAlignment.Center;
            _grip.VerticalAlignment = VerticalAlignment.Center;
        }
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = CapturePointer(e.Pointer);
        if (!_dragging) return;
        e.Handled = true;
        ShowGrip();
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        e.Handled = true;
        Dragged?.Invoke(this, e.GetCurrentPoint(PositionReference).Position);
    }

    private void EndDrag(Pointer? pointer)
    {
        if (!_dragging) return;
        _dragging = false;
        if (pointer is not null) ReleasePointerCapture(pointer);
        Paint();
        _grip.Opacity = 0;
        DragCompleted?.Invoke(this, EventArgs.Empty);
    }
}
