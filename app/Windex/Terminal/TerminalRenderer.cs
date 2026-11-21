// Draws one pane's rex_frame with Win2D, in device pixels.
//
// The work is split so the core's frame lock is held as briefly as possible:
//   Prepare (frame locked)  walks the cells once and copies everything it needs: a background
//                           colour per cell, a foreground colour per cell, glyph runs, sprites,
//                           colour glyphs, decorations, selection runs, find matches and the cursor.
//   Draw    (unlocked)      replays that into the control's drawing session.
//
// Colours cost no draw calls: per-cell backgrounds are one cols×rows bitmap scaled up with
// nearest-neighbour sampling, and glyphs are drawn white into one alpha mask that is then filled
// once through a cols×rows bitmap of foreground colours, so each row needs one glyph run per face
// however many colours it has. Every glyph run positions its glyphs by explicit advances that
// land each glyph on its own cell, so nothing drifts across a line.
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Windex.Interop;
using Windows.Foundation;
using Windows.UI;

namespace Windex.Terminal;

internal enum CursorLook { Hidden, Focused, Unfocused }

/// <summary>Per-draw inputs from the view (device pixels).</summary>
internal struct RenderOptions
{
    public int OriginX, OriginY;       // grid origin inside the surface
    public int SurfaceWidth, SurfaceHeight;
    public int Cols, Rows;             // the grid this view has room for
    public bool TransparentBackground;
    public bool FillPadding;
    public CursorLook Cursor;
    public uint CursorText;            // glyphs under a filled block cursor (premultiplied, opaque)
    public Color Selection;
    public Color? SelectionForeground;
    public int LinkRow, LinkStart, LinkEnd; // hovered link cells (LinkRow < 0: none)
    public float SelectionRadius;
    public int CursorBarWidth;
    // Find in terminal (design §7.5): every match a light accent fill under an accent outline, the
    // current one a strong accent fill with its text in the on-accent colour.
    public Color MatchFill, MatchStroke, CurrentMatchFill;
    public uint CurrentMatchText;      // premultiplied, opaque
    public float MatchRadius, MatchStrokeWidth;
}

/// <summary>What the view keeps from the last frame (read without the lock).</summary>
internal struct FrameState
{
    public bool Valid;
    public int Cols, Rows, DrawnCols, DrawnRows;
    public int CursorX, CursorY;
    public RexCursorShape CursorShape;
    public bool CursorBlink, MouseTracking, AltScreen, Paused;
    public ulong TotalRows, ViewportTop;
    public uint Fg, Bg;
    /// <summary>Find in terminal: a query is set, its counts (current: 1 = newest), still reading
    /// scrollback, or why the query cannot be searched for.</summary>
    public bool SearchActive, SearchBusy;
    public uint SearchTotal, SearchCurrent;
    public RexSearchError SearchError;
    /// <summary>The current match's cells on the drawn grid (inclusive); Row0 &lt; 0 when it is not on screen.</summary>
    public (int Row0, int Col0, int Row1, int Col1) CurrentMatch;
}

/// <summary>Timings of the last frame, in milliseconds.</summary>
public readonly record struct TerminalRenderStats(double PrepareMs, double DrawMs, int Cols, int Rows, int GlyphRuns, int Sprites)
{
    public double TotalMs => PrepareMs + DrawMs;
}

internal sealed unsafe class TerminalRenderer : IDisposable
{
    private enum Deco : byte { Underline, Double, Curly, Dotted, Dashed, Strike, Overline }

    private readonly record struct GlyphRun(int Slot, Vector2 Origin, CanvasGlyph[] Glyphs);
    private readonly record struct SpriteDraw(int Index, float X, float Y, Vector4 Tint);
    private readonly record struct ColorDraw(int Index, float X, float Y, Color Color);
    private readonly record struct DecoRun(Deco Kind, int Row, int Col0, int Col1, uint Color); // Color 0 = foreground brush
    private readonly record struct SelectionBox(int Row0, int Row1, int Col0, int Col1);
    private readonly record struct MatchBox(int Row, int Col0, int Col1, bool Current);

    private readonly CanvasDevice _device;
    private TerminalFonts _fonts;
    private CanvasRenderTarget? _atlas;
    private (int W, int H, int Light) _atlasKey;

    private int _bitmapCols, _bitmapRows;
    private byte[] _bgBytes = [];
    private byte[] _fgBytes = [];
    private CanvasBitmap? _bgBitmap, _fgBitmap;
    private CanvasImageBrush? _fgBrush;
    private CanvasRenderTarget? _mask;
    private CanvasImageBrush? _maskBrush;
    private CanvasSolidColorBrush? _white, _solid;

    private readonly List<GlyphRun> _runs = new();
    private readonly List<SpriteDraw> _sprites = new();
    private readonly List<ColorDraw> _colors = new();
    private readonly List<DecoRun> _decos = new();
    private readonly List<SelectionBox> _selection = new();
    private readonly List<MatchBox> _matches = new();
    private bool[] _selected = [];
    private readonly GlyphArrays _glyphArrays = new();
    private readonly RowRuns _rowRuns = new();
    private RenderOptions _options;
    private FrameState _state;
    private (int X, int Y, int Width) _cursorCell;
    private uint _cursorColor;
    private bool _hasFrame;
    private byte[] _title = [];
    private string _titleText = "";

    public TerminalRenderer(CanvasDevice device, TerminalFonts fonts)
    {
        _device = device;
        _fonts = fonts;
    }

    public TerminalFonts Fonts
    {
        get => _fonts;
        set => _fonts = value;
    }

    public TerminalRenderStats Stats { get; private set; }

    public string Title => _titleText;

    /// <summary>Forgets the last frame (the view switched panes).</summary>
    public void Reset()
    {
        _hasFrame = false;
        _state = default;
        _title = [];
        _titleText = "";
    }

    // ================================================================== prepare (frame locked)

    /// <summary>Copies what the next Draw needs out of a locked frame. Returns the frame's state.</summary>
    public FrameState Prepare(RexFrame* f, in RenderOptions o, out bool titleChanged)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _options = o;
        _fonts.BeginFrame();
        _runs.Clear();
        _sprites.Clear();
        _colors.Clear();
        _decos.Clear();
        _selection.Clear();
        _matches.Clear();
        _glyphArrays.Reset();

        int frameCols = f->Cols, frameRows = f->Rows;
        int cols = Math.Min(frameCols, o.Cols), rows = Math.Min(frameRows, o.Rows);
        EnsureBuffers(cols, rows);
        var bg = MemoryMarshal.Cast<byte, uint>(_bgBytes.AsSpan());
        var fg = MemoryMarshal.Cast<byte, uint>(_fgBytes.AsSpan());
        int cw = _fonts.CellWidth, ch = _fonts.CellHeight;
        uint defaultBg = o.TransparentBackground ? 0u : 0xFF000000u | f->Bg;
        uint selectionFg = o.SelectionForeground is { } sf ? 0xFF000000u | ((uint)sf.R << 16) | ((uint)sf.G << 8) | sf.B : 0;

        for (int r = 0; r < rows; r++)
        {
            RexCell* row = f->Cells + r * frameCols;
            int rowTop = o.OriginY + r * ch;
            float baseline = rowTop + _fonts.Baseline;
            int line = r * cols;
            uint wideFg = 0;
            _rowRuns.Reset();
            int selStart = -1;
            Deco? ul = null; uint ulColor = 0; int ulStart = 0;
            int strikeStart = -1, overStart = -1;

            for (int c = 0; c < cols; c++)
            {
                RexCell cell = row[c];
                var flags = cell.Flags;
                bg[line + c] = (flags & RexCellFlags.DefaultBg) != 0 ? defaultBg : 0xFF000000u | cell.Bg;

                bool selected = (flags & RexCellFlags.Selected) != 0;
                _selected[line + c] = selected;
                if (selected) { if (selStart < 0) selStart = c; }
                else if (selStart >= 0) { AddSelection(r, selStart, c - 1); selStart = -1; }

                uint color = selected && selectionFg != 0 ? selectionFg : cell.Fg;
                uint px = (flags & RexCellFlags.Faint) != 0 ? Premultiply(color, 153) : 0xFF000000u | color;
                if ((flags & RexCellFlags.Spacer) != 0) px = wideFg;
                fg[line + c] = px;

                // Decorations continue across blanks and spacers; each kind merges into runs.
                Deco? cellUl = (flags & RexCellFlags.AnyUnderline) == 0 ? null
                    : (flags & RexCellFlags.UnderlineDouble) != 0 ? Deco.Double
                    : (flags & RexCellFlags.UnderlineCurly) != 0 ? Deco.Curly
                    : (flags & RexCellFlags.UnderlineDotted) != 0 ? Deco.Dotted
                    : (flags & RexCellFlags.UnderlineDashed) != 0 ? Deco.Dashed
                    : Deco.Underline;
                uint cellUlColor = cell.UlColor == 0xFFFFFFFFu ? 0u : 0xFF000000u | cell.UlColor;
                if (cellUl != ul || cellUlColor != ulColor)
                {
                    if (ul is { } k) _decos.Add(new DecoRun(k, r, ulStart, c - 1, ulColor));
                    ul = cellUl; ulColor = cellUlColor; ulStart = c;
                }
                Track(ref strikeStart, (flags & RexCellFlags.Strike) != 0, Deco.Strike, r, c);
                Track(ref overStart, (flags & RexCellFlags.Overline) != 0, Deco.Overline, r, c);

                if ((flags & RexCellFlags.Spacer) != 0 || cell.TextLen == 0) continue;

                bool wide = (flags & RexCellFlags.Wide) != 0;
                if (wide) wideFg = px;
                int next = c + (wide ? 2 : 1);
                bool nextBlank = next >= cols || row[next].TextLen == 0;
                int style = ((flags & RexCellFlags.Bold) != 0 ? 1 : 0) | ((flags & RexCellFlags.Italic) != 0 ? 2 : 0);
                var entry = _fonts.Lookup(new ReadOnlySpan<char>(f->Text + cell.TextOff, cell.TextLen), style, wide, nextBlank);
                int x = o.OriginX + c * cw;
                switch (entry.Kind)
                {
                    case GlyphKind.Glyph:
                        _rowRuns.Add(entry.Slot, entry.Index, x, entry.Dx, entry.Dy);
                        break;
                    case GlyphKind.Composite:
                        foreach (var g in _fonts.Composites[entry.Index]) _rowRuns.Add(g.Slot, g.Index, x, g.Dx, g.Dy);
                        break;
                    case GlyphKind.Sprite:
                        _sprites.Add(new SpriteDraw(entry.Index, x, rowTop, Tint(px)));
                        break;
                    case GlyphKind.Color:
                        _colors.Add(new ColorDraw(entry.Index, x, rowTop, Straight(px)));
                        break;
                }
            }
            if (selStart >= 0) AddSelection(r, selStart, cols - 1);
            if (ul is { } last) _decos.Add(new DecoRun(last, r, ulStart, cols - 1, ulColor));
            Track(ref strikeStart, false, Deco.Strike, r, cols);
            Track(ref overStart, false, Deco.Overline, r, cols);
            _rowRuns.Flush(_runs, _glyphArrays, baseline);
        }

        var currentMatch = PrepareMatches(f, cols, rows, fg);
        PrepareCursor(f, cols, rows, fg);
        _hasFrame = true;
        titleChanged = UpdateTitle(f->Title);
        _state = new FrameState
        {
            Valid = true,
            Cols = frameCols, Rows = frameRows, DrawnCols = cols, DrawnRows = rows,
            CursorX = f->CursorX, CursorY = f->CursorY, CursorShape = f->CursorShape,
            CursorBlink = f->CursorBlink != 0, MouseTracking = f->MouseTracking != 0, AltScreen = f->AltScreen != 0,
            Paused = f->Paused != 0, TotalRows = f->TotalRows, ViewportTop = f->ViewportTop,
            Fg = f->Fg, Bg = f->Bg,
            SearchActive = f->SearchActive != 0, SearchBusy = f->SearchBusy != 0,
            SearchTotal = f->SearchTotal, SearchCurrent = f->SearchCurrent, SearchError = f->SearchError,
            CurrentMatch = currentMatch,
        };
        _prepareTicks = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        return _state;

        void Track(ref int start, bool on, Deco kind, int r, int c)
        {
            if (on && start < 0) start = c;
            else if (!on && start >= 0) { _decos.Add(new DecoRun(kind, r, start, c - 1, 0)); start = -1; }
        }
    }

    private long _prepareTicks;

    private void AddSelection(int row, int col0, int col1)
    {
        // Merge with the box above when it spans exactly the same columns (full lines).
        if (_selection.Count > 0 && _selection[^1] is var last && last.Row1 == row - 1 && last.Col0 == col0 && last.Col1 == col1)
            _selection[^1] = last with { Row1 = row };
        else
            _selection.Add(new SelectionBox(row, row, col0, col1));
    }

    /// <summary>
    /// The frame's match spans on the drawn grid. The current match's glyphs (and sprites) take the
    /// on-accent text colour, after the selection's and before a block cursor's. Returns the current
    /// match's bounding cells, Row0 = -1 when none of it is drawn.
    /// </summary>
    private (int Row0, int Col0, int Row1, int Col1) PrepareMatches(RexFrame* f, int cols, int rows, Span<uint> fg)
    {
        var current = (Row0: -1, Col0: int.MaxValue, Row1: -1, Col1: -1);
        for (uint i = 0; i < f->MatchCount; i++)
        {
            RexMatch m = f->Matches[i];
            if (m.Row >= rows || m.Col0 >= cols || m.Col1 < m.Col0) continue;
            int col1 = Math.Min((int)m.Col1, cols - 1);
            bool isCurrent = m.Current != 0;
            _matches.Add(new MatchBox(m.Row, m.Col0, col1, isCurrent));
            if (!isCurrent) continue;
            if (current.Row0 < 0) current.Row0 = m.Row;
            current = (current.Row0, Math.Min(current.Col0, m.Col0), m.Row, Math.Max(current.Col1, col1));
            uint text = _options.CurrentMatchText;
            if (text == 0) continue;
            for (int c = m.Col0; c <= col1; c++) fg[m.Row * cols + c] = text;
            Retint(m.Row, m.Col0, col1, text);
        }
        return current.Row0 < 0 ? (-1, 0, -1, 0) : current;
    }

    /// <summary>Box-drawing sprites in a run of cells take a new colour (their fg is not the bitmap's).</summary>
    private void Retint(int row, int col0, int col1, uint px)
    {
        int cw = _fonts.CellWidth;
        float x0 = _options.OriginX + col0 * cw, x1 = _options.OriginX + (col1 + 1) * cw, y = _options.OriginY + row * _fonts.CellHeight;
        for (int i = 0; i < _sprites.Count; i++)
            if (_sprites[i].Y == y && _sprites[i].X >= x0 && _sprites[i].X < x1)
                _sprites[i] = _sprites[i] with { Tint = Tint(px) };
    }

    private void PrepareCursor(RexFrame* f, int cols, int rows, Span<uint> fg)
    {
        _cursorCell = (-1, -1, 0);
        if (_options.Cursor == CursorLook.Hidden || f->CursorX < 0 || f->CursorY < 0 || f->CursorX >= cols || f->CursorY >= rows) return;
        int x = f->CursorX, y = f->CursorY;
        var cell = f->Cells[y * f->Cols + x];
        if ((cell.Flags & RexCellFlags.Spacer) != 0 && x > 0) { x--; cell = f->Cells[y * f->Cols + x]; }
        int width = (cell.Flags & RexCellFlags.Wide) != 0 && x + 1 < cols ? 2 : 1;
        _cursorCell = (x, y, width);
        _cursorColor = 0xFF000000u | f->CursorColor;
        uint text = _options.CursorText;
        if (_options.Cursor == CursorLook.Focused && f->CursorShape == RexCursorShape.Block)
        {
            // The glyph under a filled block takes the theme's cursor text colour.
            for (int i = 0; i < width; i++) fg[y * cols + x + i] = text;
            Retint(y, x, x + width - 1, text);
        }
    }

    private bool UpdateTitle(byte* title)
    {
        int len = 0;
        if (title != null) while (title[len] != 0) len++;
        var bytes = new ReadOnlySpan<byte>(title, len);
        if (bytes.SequenceEqual(_title)) return false;
        _title = bytes.ToArray();
        _titleText = System.Text.Encoding.UTF8.GetString(bytes);
        return true;
    }

    private void EnsureBuffers(int cols, int rows)
    {
        if (cols == _bitmapCols && rows == _bitmapRows && _bgBitmap is not null) return;
        _bitmapCols = Math.Max(1, cols);
        _bitmapRows = Math.Max(1, rows);
        int n = _bitmapCols * _bitmapRows;
        _bgBytes = new byte[n * 4];
        _fgBytes = new byte[n * 4];
        _selected = new bool[n];
        _bgBitmap?.Dispose();
        _fgBitmap?.Dispose();
        _fgBrush?.Dispose();
        _bgBitmap = CanvasBitmap.CreateFromBytes(_device, _bgBytes, _bitmapCols, _bitmapRows, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
        _fgBitmap = CanvasBitmap.CreateFromBytes(_device, _fgBytes, _bitmapCols, _bitmapRows, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
        _fgBrush = new CanvasImageBrush(_device, _fgBitmap)
        {
            Interpolation = CanvasImageInterpolation.NearestNeighbor,
            ExtendX = CanvasEdgeBehavior.Clamp,
            ExtendY = CanvasEdgeBehavior.Clamp,
        };
    }

    // ================================================================== draw (unlocked)

    /// <summary>
    /// Draws the prepared frame (or only the background when there is none) into a session that
    /// covers the options' surface size in device pixels and starts cleared to transparent.
    /// <paramref name="o"/> is the same options Prepare got this frame (when there was a frame).
    /// Flushes before returning, so DrawMs includes Direct2D's own work.
    /// </summary>
    public void Draw(CanvasDrawingSession ds, in RenderOptions o, uint placeholderBg)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _options = o;
        bool frame = _hasFrame && _state.DrawnCols > 0 && _state.DrawnRows > 0 && o.SurfaceWidth > 0 && o.SurfaceHeight > 0;
        if (frame) DrawTextMask(o.SurfaceWidth, o.SurfaceHeight);
        ds.Units = CanvasUnits.Pixels;
        ds.TextAntialiasing = CanvasTextAntialiasing.Grayscale;
        ds.Antialiasing = CanvasAntialiasing.Aliased;
        if (!o.TransparentBackground) ds.Clear(Straight(0xFF000000u | (_hasFrame ? _state.Bg : placeholderBg)));
        if (frame) DrawFrame(ds, o);
        ds.Flush();
        double ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        Stats = new TerminalRenderStats(_prepareTicks * ms, (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * ms,
            _state.DrawnCols, _state.DrawnRows, _runs.Count, _sprites.Count);
    }

    /// <summary>
    /// Glyphs and foreground-coloured decorations, drawn white into an alpha mask the size of the
    /// surface. DrawFrame then fills the mask once through the per-cell foreground colours: drawing
    /// each glyph run through an image brush instead costs D2D a coverage pass per run.
    /// </summary>
    private void DrawTextMask(int width, int height)
    {
        if (_mask is null || _mask.SizeInPixels.Width != width || _mask.SizeInPixels.Height != height)
        {
            _mask?.Dispose();
            _maskBrush?.Dispose();
            _mask = new CanvasRenderTarget(_device, width, height, 96,
                Windows.Graphics.DirectX.DirectXPixelFormat.A8UIntNormalized, CanvasAlphaMode.Premultiplied);
            _maskBrush = new CanvasImageBrush(_device, _mask) { Interpolation = CanvasImageInterpolation.NearestNeighbor };
        }
        _white ??= new CanvasSolidColorBrush(_device, Colors.White);
        using var ds = _mask.CreateDrawingSession();
        ds.Clear(Colors.Transparent);
        ds.Units = CanvasUnits.Pixels;
        ds.TextAntialiasing = CanvasTextAntialiasing.Grayscale;
        ds.Antialiasing = CanvasAntialiasing.Aliased;
        foreach (var run in _runs)
        {
            var slot = _fonts.Slots[run.Slot];
            ds.DrawGlyphRun(run.Origin, slot.Face, slot.Size, run.Glyphs, false, 0, _white);
        }
        DrawDecorations(ds, _options, _white, foregroundColoured: true);
    }

    private void DrawFrame(CanvasDrawingSession ds, in RenderOptions o)
    {
        int cols = _state.DrawnCols, rows = _state.DrawnRows;
        int cw = _fonts.CellWidth, ch = _fonts.CellHeight;
        float gridW = cols * cw, gridH = rows * ch;

        // Backgrounds: one bitmap, one pixel per cell, scaled up without filtering.
        _bgBitmap!.SetPixelBytes(_bgBytes);
        var src = new Rect(0, 0, cols, rows);
        ds.DrawImage(_bgBitmap, new Rect(o.OriginX, o.OriginY, gridW, gridH), src, 1, CanvasImageInterpolation.NearestNeighbor);
        if (o.FillPadding) FillPadding(ds, o, cols, rows, gridW, gridH);

        DrawSelection(ds, o, cols, rows);
        DrawMatches(ds, o);

        if (_cursorCell.X >= 0 && o.Cursor == CursorLook.Focused && _state.CursorShape == RexCursorShape.Block)
            ds.FillRectangle(CellRect(_cursorCell.X, _cursorCell.Y, _cursorCell.Width), Straight(_cursorColor));

        // Text: the white mask filled through the foreground colours, one pixel per cell scaled up.
        _fgBitmap!.SetPixelBytes(_fgBytes);
        var brush = _fgBrush!;
        brush.Transform = Matrix3x2.CreateScale(cw, ch) * Matrix3x2.CreateTranslation(o.OriginX, o.OriginY);
        ds.FillRectangle(new Rect(0, 0, o.SurfaceWidth, o.SurfaceHeight), brush, _maskBrush!);

        if (_sprites.Count > 0)
        {
            var atlas = Atlas();
            using var batch = ds.CreateSpriteBatch(CanvasSpriteSortMode.None, CanvasImageInterpolation.NearestNeighbor, CanvasSpriteOptions.None);
            foreach (var s in _sprites)
                batch.DrawFromSpriteSheet(atlas, new Rect(s.X, s.Y, cw, ch), TerminalSprites.SourceRect(s.Index, cw, ch), s.Tint);
        }

        foreach (var c in _colors)
        {
            var glyph = _fonts.Colors[c.Index];
            ds.DrawTextLayout(glyph.Layout, c.X + glyph.Dx, c.Y + glyph.Top, c.Color);
        }

        DrawDecorations(ds, o, brush, foregroundColoured: false);

        if (_cursorCell.X >= 0) DrawCursor(ds, o);
    }

    private Rect CellRect(int col, int row, int width) =>
        new(_options.OriginX + col * _fonts.CellWidth, _options.OriginY + row * _fonts.CellHeight, width * _fonts.CellWidth, _fonts.CellHeight);

    /// <summary>
    /// Edge rows and columns with a program background extend into the padding. The grid is
    /// top-left aligned, so its left and top edges always meet the padding; its right (bottom) edge
    /// does only when the frame and the view have the same number of columns (rows). A smaller
    /// frame (another client owns the size, or the frame still lags a resize) leaves the unused
    /// area unpainted rather than smearing its last column or row across it, and a larger one is
    /// clipped there, so its last drawn column is not an edge either.
    /// </summary>
    private void FillPadding(CanvasDrawingSession ds, in RenderOptions o, int cols, int rows, float gridW, float gridH)
    {
        bool rightEdge = cols == _state.Cols && cols == o.Cols, bottomEdge = rows == _state.Rows && rows == o.Rows;
        float left = o.OriginX, top = o.OriginY, right = o.SurfaceWidth - (o.OriginX + gridW), bottom = o.SurfaceHeight - (o.OriginY + gridH);
        var bmp = _bgBitmap!;
        void Stretch(double dx, double dy, double dw, double dh, double sx, double sy, double sw, double sh)
        {
            if (dw > 0 && dh > 0) ds.DrawImage(bmp, new Rect(dx, dy, dw, dh), new Rect(sx, sy, sw, sh), 1, CanvasImageInterpolation.NearestNeighbor);
        }
        float gx = o.OriginX + gridW, gy = o.OriginY + gridH;
        Stretch(0, top, left, gridH, 0, 0, 1, rows);
        Stretch(left, 0, gridW, top, 0, 0, cols, 1);
        Stretch(0, 0, left, top, 0, 0, 1, 1);
        if (rightEdge)
        {
            Stretch(gx, top, right, gridH, cols - 1, 0, 1, rows);
            Stretch(gx, 0, right, top, cols - 1, 0, 1, 1);
        }
        if (bottomEdge)
        {
            Stretch(left, gy, gridW, bottom, 0, rows - 1, cols, 1);
            Stretch(0, gy, left, bottom, 0, rows - 1, 1, 1);
        }
        if (rightEdge && bottomEdge) Stretch(gx, gy, right, bottom, cols - 1, rows - 1, 1, 1);
    }

    /// <summary>Selection boxes with rounded outer corners (Rex parity); inner corners stay square.</summary>
    private void DrawSelection(CanvasDrawingSession ds, in RenderOptions o, int cols, int rows)
    {
        if (_selection.Count == 0) return;
        ds.Antialiasing = CanvasAntialiasing.Antialiased;
        float r = o.SelectionRadius;
        bool Sel(int row, int col) => row >= 0 && row < rows && col >= 0 && col < cols && _selected[row * cols + col];
        foreach (var box in _selection)
        {
            var rect = CellRect(box.Col0, box.Row0, box.Col1 - box.Col0 + 1);
            rect.Height = (box.Row1 - box.Row0 + 1) * _fonts.CellHeight;
            float tl = Sel(box.Row0 - 1, box.Col0) ? 0 : r, tr = Sel(box.Row0 - 1, box.Col1) ? 0 : r;
            float bl = Sel(box.Row1 + 1, box.Col0) ? 0 : r, br = Sel(box.Row1 + 1, box.Col1) ? 0 : r;
            using var geometry = RoundedRect(ds, rect, tl, tr, br, bl);
            ds.FillGeometry(geometry, o.Selection);
        }
        ds.Antialiasing = CanvasAntialiasing.Aliased;
    }

    /// <summary>Find matches over the selection (so the current one stays readable inside it) and under the text.</summary>
    private void DrawMatches(CanvasDrawingSession ds, in RenderOptions o)
    {
        if (_matches.Count == 0) return;
        ds.Antialiasing = CanvasAntialiasing.Antialiased;
        float r = o.MatchRadius, line = o.MatchStrokeWidth;
        foreach (var m in _matches)
        {
            var rect = CellRect(m.Col0, m.Row, m.Col1 - m.Col0 + 1);
            if (m.Current)
            {
                ds.FillRoundedRectangle(rect, r, r, o.CurrentMatchFill);
                continue;
            }
            ds.FillRoundedRectangle(rect, r, r, o.MatchFill);
            var edge = new Rect(rect.X + line / 2, rect.Y + line / 2, rect.Width - line, rect.Height - line);
            ds.DrawRoundedRectangle(edge, Math.Max(0, r - line / 2), Math.Max(0, r - line / 2), o.MatchStroke, line);
        }
        ds.Antialiasing = CanvasAntialiasing.Aliased;
    }

    private static CanvasGeometry RoundedRect(ICanvasResourceCreator rc, Rect rect, float tl, float tr, float br, float bl)
    {
        float x0 = (float)rect.X, y0 = (float)rect.Y, x1 = (float)(rect.X + rect.Width), y1 = (float)(rect.Y + rect.Height);
        float max = MathF.Min(x1 - x0, y1 - y0) / 2;
        tl = MathF.Min(tl, max); tr = MathF.Min(tr, max); br = MathF.Min(br, max); bl = MathF.Min(bl, max);
        using var p = new CanvasPathBuilder(rc);
        p.BeginFigure(x0 + tl, y0);
        p.AddLine(x1 - tr, y0);
        if (tr > 0) p.AddArc(new Vector2(x1, y0 + tr), tr, tr, 0, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
        p.AddLine(x1, y1 - br);
        if (br > 0) p.AddArc(new Vector2(x1 - br, y1), br, br, 0, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
        p.AddLine(x0 + bl, y1);
        if (bl > 0) p.AddArc(new Vector2(x0, y1 - bl), bl, bl, 0, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
        p.AddLine(x0, y0 + tl);
        if (tl > 0) p.AddArc(new Vector2(x0 + tl, y0), tl, tl, 0, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
        p.EndFigure(CanvasFigureLoop.Closed);
        return CanvasGeometry.CreatePath(p);
    }

    /// <summary>
    /// Underlines, strikes and overlines. Those in the cell's own foreground colour go into the text
    /// mask (<paramref name="foregroundColoured"/>, drawn with <paramref name="brush"/>); those with
    /// an explicit underline colour are drawn directly in it.
    /// </summary>
    private void DrawDecorations(CanvasDrawingSession ds, in RenderOptions o, ICanvasBrush brush, bool foregroundColoured)
    {
        int cw = _fonts.CellWidth, ch = _fonts.CellHeight;
        int thick = _fonts.UnderlineThickness;
        foreach (var d in _decos)
        {
            if ((d.Color == 0) != foregroundColoured) continue;
            float x0 = o.OriginX + d.Col0 * cw, x1 = o.OriginX + (d.Col1 + 1) * cw, top = o.OriginY + d.Row * ch;
            if (!foregroundColoured) brush = Solid(d.Color);
            switch (d.Kind)
            {
                case Deco.Underline:
                    ds.FillRectangle(x0, top + _fonts.UnderlineTop, x1 - x0, thick, brush);
                    break;
                case Deco.Double:
                {
                    int y2 = Math.Min(_fonts.UnderlineTop + 2 * thick, ch - thick);
                    int y1 = Math.Max(0, y2 - 2 * thick);
                    ds.FillRectangle(x0, top + y1, x1 - x0, thick, brush);
                    ds.FillRectangle(x0, top + y2, x1 - x0, thick, brush);
                    break;
                }
                case Deco.Curly:
                    DrawCurly(ds, x0, x1, top, brush);
                    break;
                case Deco.Dotted:
                case Deco.Dashed:
                {
                    // Whole-pixel dashes keep dots crisp: dots are square, dashes are three dots long.
                    int dash = d.Kind == Deco.Dotted ? thick : Math.Max(3 * thick, cw / 2);
                    int gap = d.Kind == Deco.Dotted ? thick : Math.Max(2 * thick, cw / 4);
                    float y = top + _fonts.UnderlineTop;
                    for (float x = x0; x < x1; x += dash + gap)
                        ds.FillRectangle(x, y, MathF.Min(dash, x1 - x), thick, brush);
                    break;
                }
                case Deco.Strike:
                    ds.FillRectangle(x0, top + _fonts.StrikeTop, x1 - x0, _fonts.StrikeThickness, brush);
                    break;
                case Deco.Overline:
                    ds.FillRectangle(x0, top, x1 - x0, thick, brush);
                    break;
            }
        }
        if (foregroundColoured && o.LinkRow >= 0 && o.LinkRow < _state.DrawnRows)
        {
            float x0 = o.OriginX + o.LinkStart * cw, x1 = o.OriginX + (o.LinkEnd + 1) * cw;
            ds.FillRectangle(x0, o.OriginY + o.LinkRow * ch + _fonts.UnderlineTop, x1 - x0, thick, brush);
        }
    }

    private CanvasSolidColorBrush Solid(uint argb)
    {
        _solid ??= new CanvasSolidColorBrush(_device, Colors.White);
        _solid.Color = Straight(argb);
        return _solid;
    }

    /// <summary>A wave one cell long per period, antialiased, sitting just under the baseline.</summary>
    private void DrawCurly(CanvasDrawingSession ds, float x0, float x1, float top, ICanvasBrush brush)
    {
        int thick = _fonts.UnderlineThickness;
        float amplitude = MathF.Max(1, thick * 1.5f);
        float mid = top + MathF.Min(_fonts.UnderlineTop + amplitude, _fonts.CellHeight - amplitude - thick / 2f);
        float half = _fonts.CellWidth / 2f;
        using var p = new CanvasPathBuilder(ds);
        p.BeginFigure(x0, mid);
        float sign = -1;
        for (float x = x0; x < x1 - 0.01f; x += half, sign = -sign)
            p.AddQuadraticBezier(new Vector2(x + half / 2, mid + sign * amplitude * 2), new Vector2(MathF.Min(x + half, x1), mid));
        p.EndFigure(CanvasFigureLoop.Open);
        using var geometry = CanvasGeometry.CreatePath(p);
        ds.Antialiasing = CanvasAntialiasing.Antialiased;
        ds.DrawGeometry(geometry, brush, thick);
        ds.Antialiasing = CanvasAntialiasing.Aliased;
    }

    private void DrawCursor(CanvasDrawingSession ds, in RenderOptions o)
    {
        var rect = CellRect(_cursorCell.X, _cursorCell.Y, _cursorCell.Width);
        var color = Straight(_cursorColor);
        float line = Math.Max(1, (int)MathF.Round(_fonts.Scale));
        if (o.Cursor == CursorLook.Unfocused || _state.CursorShape == RexCursorShape.Hollow)
        {
            if (o.Cursor == CursorLook.Unfocused) color.A = 153;
            ds.DrawRectangle((float)rect.X + line / 2, (float)rect.Y + line / 2, (float)rect.Width - line, (float)rect.Height - line, color, line);
            return;
        }
        switch (_state.CursorShape)
        {
            case RexCursorShape.Bar:
                ds.FillRectangle((float)rect.X, (float)rect.Y, o.CursorBarWidth, (float)rect.Height, color);
                break;
            case RexCursorShape.Underline:
                ds.FillRectangle((float)rect.X, (float)(rect.Y + rect.Height - o.CursorBarWidth), (float)rect.Width, o.CursorBarWidth, color);
                break;
        }
    }

    private CanvasRenderTarget Atlas()
    {
        var key = (_fonts.CellWidth, _fonts.CellHeight, _fonts.LightLine);
        if (_atlas is null || _atlasKey != key)
        {
            _atlas?.Dispose();
            _atlas = TerminalSprites.CreateAtlas(_device, key.CellWidth, key.CellHeight, key.LightLine);
            _atlasKey = key;
        }
        return _atlas;
    }

    // ================================================================== colours

    private static uint Premultiply(uint rgb, uint a) =>
        (a << 24) | ((((rgb >> 16) & 0xFF) * a / 255) << 16) | ((((rgb >> 8) & 0xFF) * a / 255) << 8) | ((rgb & 0xFF) * a / 255);

    /// <summary>A premultiplied pixel as a sprite tint (premultiplied components).</summary>
    private static Vector4 Tint(uint px) =>
        new(((px >> 16) & 0xFF) / 255f, ((px >> 8) & 0xFF) / 255f, (px & 0xFF) / 255f, (px >> 24) / 255f);

    /// <summary>A premultiplied pixel as a straight-alpha colour.</summary>
    private static Color Straight(uint px)
    {
        byte a = (byte)(px >> 24);
        if (a == 0) return Colors.Transparent;
        if (a == 255) return Color.FromArgb(255, (byte)(px >> 16), (byte)(px >> 8), (byte)px);
        return Color.FromArgb(a, (byte)(((px >> 16) & 0xFF) * 255 / a), (byte)(((px >> 8) & 0xFF) * 255 / a), (byte)((px & 0xFF) * 255 / a));
    }

    public void Dispose()
    {
        _atlas?.Dispose();
        _bgBitmap?.Dispose();
        _fgBitmap?.Dispose();
        _fgBrush?.Dispose();
        _mask?.Dispose();
        _maskBrush?.Dispose();
        _white?.Dispose();
        _solid?.Dispose();
    }

    // ================================================================== glyph run building

    /// <summary>The glyphs of one row, grouped by face slot, each at its cell's x.</summary>
    private sealed class RowRuns
    {
        private sealed class Builder
        {
            public int Slot;
            public int Count;
            public int[] Index = new int[64];
            public float[] X = new float[64], Dx = new float[64], Dy = new float[64];

            public void Add(int index, float x, float dx, float dy)
            {
                if (Count == Index.Length)
                {
                    Array.Resize(ref Index, Count * 2);
                    Array.Resize(ref X, Count * 2);
                    Array.Resize(ref Dx, Count * 2);
                    Array.Resize(ref Dy, Count * 2);
                }
                Index[Count] = index; X[Count] = x; Dx[Count] = dx; Dy[Count] = dy;
                Count++;
            }
        }

        private readonly List<Builder> _builders = new();
        private int _active;

        public void Reset() => _active = 0;

        public void Add(int slot, int index, float x, float dx, float dy)
        {
            for (int i = 0; i < _active; i++)
                if (_builders[i].Slot == slot) { _builders[i].Add(index, x, dx, dy); return; }
            if (_active == _builders.Count) _builders.Add(new Builder());
            var b = _builders[_active++];
            b.Slot = slot;
            b.Count = 0;
            b.Add(index, x, dx, dy);
        }

        /// <summary>Turns each slot's glyphs into one run whose advances land every glyph on its cell.</summary>
        public void Flush(List<GlyphRun> runs, GlyphArrays arrays, float baseline)
        {
            for (int i = 0; i < _active; i++)
            {
                var b = _builders[i];
                var glyphs = arrays.Rent(b.Count);
                for (int g = 0; g < b.Count; g++)
                {
                    glyphs[g] = new CanvasGlyph
                    {
                        Index = b.Index[g],
                        Advance = g + 1 < b.Count ? b.X[g + 1] - b.X[g] : 0,
                        AdvanceOffset = b.Dx[g],
                        AscenderOffset = b.Dy[g],
                    };
                }
                runs.Add(new GlyphRun(b.Slot, new Vector2(b.X[0], baseline), glyphs));
            }
            _active = 0;
        }
    }

    /// <summary>Exact-length glyph arrays, reused frame to frame (each run must own its array until drawn).</summary>
    private sealed class GlyphArrays
    {
        private readonly Dictionary<int, (List<CanvasGlyph[]> Arrays, int Used)> _byLength = new();
        private readonly List<int> _usedLengths = new();

        public CanvasGlyph[] Rent(int length)
        {
            if (!_byLength.TryGetValue(length, out var pool)) pool = (new List<CanvasGlyph[]>(), 0);
            if (pool.Used == 0) _usedLengths.Add(length);
            if (pool.Used == pool.Arrays.Count) pool.Arrays.Add(new CanvasGlyph[length]);
            var array = pool.Arrays[pool.Used];
            _byLength[length] = (pool.Arrays, pool.Used + 1);
            return array;
        }

        public void Reset()
        {
            foreach (var length in _usedLengths)
                _byLength[length] = (_byLength[length].Arrays, 0);
            _usedLengths.Clear();
        }
    }
}
