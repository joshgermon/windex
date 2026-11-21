// Pointer input. While the program tracks the mouse (and Shift is up) presses, releases, moves and
// wheel notches go to Core.Mouse; otherwise the pointer selects (Core.Select: press with the click
// count, drag — also outside the grid, which autoscrolls — and release) and the wheel scrolls the
// viewport. Ctrl+wheel is left unhandled for the host's font zoom. Ctrl over a link underlines it;
// Ctrl+click opens it. Positions are device pixels
// relative to the cell grid's top-left corner, the unit rex_pane_mouse and rex_pane_select use.
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windex.Core;
using Windex.Interop;
using Windows.System;

namespace Windex.Terminal;

public sealed partial class TerminalView
{
    private readonly record struct LinkHover(int Row, int Start, int End, string Target);

    private LinkHover? _hover;
    private (int Col, int Row) _hoverCell = (-1, -1);
    private bool _selecting;
    private RexMouseButton _mouseButton;
    private int _clickCount;
    private long _lastClickTime;
    private Windows.Foundation.Point _lastClickPoint;
    private double _wheelRows;
    private (float X, float Y) _lastGridPoint;

    private static readonly InputCursor TextCursor = InputSystemCursor.Create(InputSystemCursorShape.IBeam);
    private static readonly InputCursor HandCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);

    private void InitializePointer()
    {
        ProtectedCursor = TextCursor;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += (_, _) => EndPointerGesture();
        PointerExited += (_, _) => { if (!_selecting) _scrollBar.SetExpanded(false); ClearHover(); };
        PointerWheelChanged += OnPointerWheelChanged;
    }

    private (float X, float Y) GridPoint(PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(this).Position;
        return _lastGridPoint = ((float)(p.X * _scale) - _originX, (float)(p.Y * _scale) - _originY);
    }

    private (int Col, int Row) CellAt(float x, float y) =>
        _fonts is null ? (-1, -1) : ((int)MathF.Floor(x / _fonts.CellWidth), (int)MathF.Floor(y / _fonts.CellHeight));

    private bool InGrid(int col, int row) => _frame.Valid && col >= 0 && row >= 0 && col < _frame.DrawnCols && row < _frame.DrawnRows;

    private static RexMods Mods(VirtualKeyModifiers m) =>
        (m.HasFlag(VirtualKeyModifiers.Shift) ? RexMods.Shift : 0) | (m.HasFlag(VirtualKeyModifiers.Control) ? RexMods.Ctrl : 0)
        | (m.HasFlag(VirtualKeyModifiers.Menu) ? RexMods.Alt : 0) | (m.HasFlag(VirtualKeyModifiers.Windows) ? RexMods.Super : 0);

    /// <summary>The program gets the mouse while it tracks it, unless Shift is held (Shift selects).</summary>
    private bool ReportsMouse(VirtualKeyModifiers mods) => _frame.MouseTracking && !mods.HasFlag(VirtualKeyModifiers.Shift);

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _input.Focus(Microsoft.UI.Xaml.FocusState.Pointer);
        if (Core is not { } core || Pane == 0) return;
        var point = e.GetCurrentPoint(this);
        var (x, y) = GridPoint(e);
        var button = point.Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonPressed => RexMouseButton.Left,
            PointerUpdateKind.RightButtonPressed => RexMouseButton.Right,
            PointerUpdateKind.MiddleButtonPressed => RexMouseButton.Middle,
            _ => RexMouseButton.None,
        };
        if (button == RexMouseButton.None) return;
        e.Handled = true;

        if (button == RexMouseButton.Left && e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control) && LinkAt(x, y) is { } link)
        {
            OpenLink(link.Target);
            return;
        }
        if (ReportsMouse(e.KeyModifiers))
        {
            _mouseButton = button;
            CapturePointer(e.Pointer);
            core.Mouse(Pane, new RexMouse { Action = RexMouseAction.Press, Button = button, Mods = Mods(e.KeyModifiers), X = x, Y = y });
            return;
        }
        if (button == RexMouseButton.Right && RightClickPastes) { Paste(); return; }
        if (button != RexMouseButton.Left) return;

        _clickCount = NextClickCount(point.Position);
        _selecting = true;
        CapturePointer(e.Pointer);
        core.Select(Pane, RexSelectOp.Press, _clickCount, x, y);
    }

    private int NextClickCount(Windows.Foundation.Point at)
    {
        long now = Environment.TickCount64;
        double slopX = KeyboardMap.GetSystemMetrics(KeyboardMap.SM_CXDOUBLECLK) / 2.0 / _scale;
        double slopY = KeyboardMap.GetSystemMetrics(KeyboardMap.SM_CYDOUBLECLK) / 2.0 / _scale;
        bool repeat = now - _lastClickTime <= KeyboardMap.GetDoubleClickTime()
            && Math.Abs(at.X - _lastClickPoint.X) <= slopX && Math.Abs(at.Y - _lastClickPoint.Y) <= slopY;
        _lastClickTime = now;
        _lastClickPoint = at;
        return repeat ? _clickCount % 3 + 1 : 1;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (Core is not { } core || Pane == 0) return;
        var (x, y) = GridPoint(e);
        var position = e.GetCurrentPoint(this).Position;
        if (!_selecting) _scrollBar.SetExpanded(ActualWidth - position.X <= TerminalScrollBar.HoverZone && position.X >= 0);

        if (_selecting)
        {
            core.Select(Pane, RexSelectOp.Drag, 0, x, y);
        }
        else if (ReportsMouse(e.KeyModifiers))
        {
            core.Mouse(Pane, new RexMouse { Action = RexMouseAction.Move, Button = _mouseButton, Mods = Mods(e.KeyModifiers), X = x, Y = y });
        }
        UpdateHover(e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control) && !_selecting ? (x, y) : null);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (Core is not { } core || Pane == 0) return;
        var (x, y) = GridPoint(e);
        if (_selecting)
        {
            e.Handled = true;
            _selecting = false;
            core.Select(Pane, RexSelectOp.Release, 0, x, y);
            ReleasePointerCapture(e.Pointer);
            if (CopyOnSelect) Copy();
        }
        else if (_mouseButton != RexMouseButton.None)
        {
            e.Handled = true;
            core.Mouse(Pane, new RexMouse { Action = RexMouseAction.Release, Button = _mouseButton, Mods = Mods(e.KeyModifiers), X = x, Y = y });
            _mouseButton = RexMouseButton.None;
            ReleasePointerCapture(e.Pointer);
        }
    }

    private void EndPointerGesture()
    {
        if (_selecting) Core?.Select(Pane, RexSelectOp.Release, 0, _lastGridPoint.X, _lastGridPoint.Y);
        _selecting = false;
        _mouseButton = RexMouseButton.None;
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (Core is not { } core || Pane == 0) return;
        var props = e.GetCurrentPoint(this).Properties;
        // Ctrl+wheel is the host's font zoom (design §11): it bubbles on unhandled.
        if (props.IsHorizontalMouseWheel || e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control)) return;
        e.Handled = true;
        double notches = props.MouseWheelDelta / 120.0;
        var (x, y) = GridPoint(e);
        if (ReportsMouse(e.KeyModifiers))
        {
            core.Mouse(Pane, new RexMouse { Action = RexMouseAction.Wheel, Mods = Mods(e.KeyModifiers), X = x, Y = y, WheelDy = (float)notches });
            return;
        }
        // Three rows per notch; precision touchpads send fractions, which accumulate into rows.
        _wheelRows += notches * 3;
        int rows = (int)Math.Truncate(_wheelRows);
        if (rows == 0) return;
        _wheelRows -= rows;
        if (_frame.AltScreen)
        {
            // No scrollback on the alternate screen: scroll the program instead (less, man, editors).
            string code = rows > 0 ? "ArrowUp" : "ArrowDown";
            for (int i = 0; i < Math.Abs(rows); i++)
            {
                core.Key(Pane, new KeyInput(code, code, null, RexMods.None, RexKeyAction.Press));
                core.Key(Pane, new KeyInput(code, code, null, RexMods.None, RexKeyAction.Release));
            }
            return;
        }
        core.Scroll(Pane, RexScrollOp.Delta, -rows);
        _scrollBar.Flash();
    }

    // ------------------------------------------------------------------ links

    private void UpdateHover((float X, float Y)? at)
    {
        if (at is not { } p) { ClearHover(); return; }
        var cell = CellAt(p.X, p.Y);
        if (cell == _hoverCell) return;
        _hoverCell = cell;
        var link = LinkAt(p.X, p.Y);
        if (link == _hover) return;
        _hover = link;
        ProtectedCursor = link is null ? TextCursor : HandCursor;
        ToolTipService.SetToolTip(this, link?.Target);
        Invalidate();
    }

    private void ClearHover()
    {
        _hoverCell = (-1, -1);
        if (_hover is null) return;
        _hover = null;
        ProtectedCursor = TextCursor;
        ToolTipService.SetToolTip(this, null);
        Invalidate();
    }

    /// <summary>The link under a grid point: an OSC 8 hyperlink or a URL in the text, with its cells on that row.</summary>
    private unsafe LinkHover? LinkAt(float x, float y)
    {
        var (col, row) = CellAt(x, y);
        if (!InGrid(col, row) || Core is not { } core) return null;
        var target = core.HyperlinkAt(Pane, col, row);
        if (string.IsNullOrEmpty(target)) return null;
        RexFrame* frame = core.LockFrame(Pane);
        if (frame is null) return null;
        try
        {
            return LinkExtent(frame, col, row, target) is var (start, end) ? new LinkHover(row, start, end, target) : null;
        }
        finally
        {
            core.UnlockFrame(Pane);
        }
    }

    private static unsafe (int Start, int End)? LinkExtent(RexFrame* f, int col, int row, string target)
    {
        if (row >= f->Rows || col >= f->Cols) return null;
        RexCell* cells = f->Cells + row * f->Cols;
        if ((cells[col].Flags & RexCellFlags.Link) != 0)
        {
            int s = col, e = col;
            while (s > 0 && (cells[s - 1].Flags & RexCellFlags.Link) != 0) s--;
            while (e + 1 < f->Cols && (cells[e + 1].Flags & RexCellFlags.Link) != 0) e++;
            return (s, e);
        }
        // A URL in the text: find the occurrence of the target that covers the column.
        var text = new System.Text.StringBuilder(f->Cols);
        var columnOf = new List<int>(f->Cols);
        for (int c = 0; c < f->Cols; c++)
        {
            var cell = cells[c];
            if ((cell.Flags & RexCellFlags.Spacer) != 0) continue;
            int before = text.Length;
            if (cell.TextLen == 0) text.Append(' ');
            else text.Append(new ReadOnlySpan<char>(f->Text + cell.TextOff, cell.TextLen));
            for (int i = before; i < text.Length; i++) columnOf.Add(c);
        }
        var line = text.ToString();
        for (int at = line.IndexOf(target, StringComparison.Ordinal); at >= 0; at = line.IndexOf(target, at + 1, StringComparison.Ordinal))
        {
            int start = columnOf[at], end = columnOf[at + target.Length - 1];
            if (start <= col && col <= end) return (start, end);
        }
        return null;
    }

    private void OpenLink(string target)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri)) return;
        var args = new TerminalLinkEventArgs(uri);
        LinkActivated?.Invoke(this, args);
        if (args.Handled) return;
        if (uri.Scheme is "http" or "https" or "mailto") _ = Launcher.LaunchUriAsync(uri);
    }
}
