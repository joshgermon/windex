// Box drawing (U+2500–257F), block elements (U+2580–259F) and Powerline separators
// (U+E0B0–E0BF) drawn as shapes on whole device pixels, as Ghostty's sprites are (the rules are
// ported from Rex Rune's sprites.rs, MIT). Font glyphs rarely fill a cell exactly, so their lines
// leave seams between neighbouring cells; these meet their neighbours' edges whatever the font.
//
// Every sprite is rendered once per cell size, white on transparent, into an atlas; the renderer
// tints atlas cells with each cell's colour through a sprite batch, 1:1 and nearest-neighbour.
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI;
using Windows.Foundation;
using Windows.UI;

namespace Windex.Terminal;

internal static class TerminalSprites
{
    public const int AtlasColumns = 16;
    public const int Count = 0x80 + 0x20 + 0x10;
    public const int AtlasRows = (Count + AtlasColumns - 1) / AtlasColumns;

    public static bool Has(int cp) => cp is >= 0x2500 and <= 0x259F or >= 0xE0B0 and <= 0xE0BF;

    public static int IndexOf(int cp) => cp >= 0xE0B0 ? 0xA0 + (cp - 0xE0B0) : cp - 0x2500;

    public static Rect SourceRect(int index, int cellWidth, int cellHeight) =>
        new((index % AtlasColumns) * cellWidth, (index / AtlasColumns) * cellHeight, cellWidth, cellHeight);

    /// <summary>Renders every sprite for a cell size into a new atlas (pixel units, DPI 96).</summary>
    public static CanvasRenderTarget CreateAtlas(CanvasDevice device, int cellWidth, int cellHeight, int light)
    {
        var atlas = new CanvasRenderTarget(device, AtlasColumns * cellWidth, AtlasRows * cellHeight, 96);
        using var ds = atlas.CreateDrawingSession();
        ds.Clear(Colors.Transparent);
        ds.Units = CanvasUnits.Pixels;
        for (int i = 0; i < Count; i++)
        {
            var src = SourceRect(i, cellWidth, cellHeight);
            using var layer = ds.CreateLayer(1f, src);
            var cell = new Cell(ds, (float)src.X, (float)src.Y, cellWidth, cellHeight, light);
            int cp = i >= 0xA0 ? 0xE0B0 + (i - 0xA0) : 0x2500 + i;
            if (cp >= 0xE0B0) cell.Powerline(cp);
            else if (cp >= 0x2580) cell.Block(cp);
            else cell.Box(cp);
        }
        return atlas;
    }

    private enum Weight : byte { None, Light, Heavy, Double }

    private enum DashGap : byte { Four, Light, Heavy }

    /// <summary>
    /// U+2500–257F as Ghostty draws them: the line from each edge to the centre as
    /// <c>up right down left</c> (. none, l light, h heavy, d double). <c>....</c> marks the dashed
    /// lines, rounded corners and diagonals, which <see cref="Cell.Box"/> handles itself.
    /// </summary>
    private const string BoxLines =
        ".l.l .h.h l.l. h.h. .... .... .... .... .... .... .... .... .ll. .hl. .lh. .hh. " +
        "..ll ..lh ..hl ..hh ll.. lh.. hl.. hh.. l..l l..h h..l h..h lll. lhl. hll. llh. " +
        "hlh. hhl. lhh. hhh. l.ll l.lh h.ll l.hl h.hl h.lh l.hh h.hh .lll .llh .hll .hlh " +
        ".lhl .lhh .hhl .hhh ll.l ll.h lh.l lh.h hl.l hl.h hh.l hh.h llll lllh lhll lhlh " +
        "hlll llhl hlhl hllh hhll llhh lhhl hhlh lhhh hlhh hhhl hhhh .... .... .... .... " +
        ".d.d d.d. .dl. .ld. .dd. ..ld ..dl ..dd ld.. dl.. dd.. l..d d..l d..d ldl. dld. " +
        "ddd. l.ld d.dl d.dd .dld .ldl .ddd ld.d dl.l dd.d ldld dldl dddd .... .... .... " +
        ".... .... .... .... ...l l... .l.. ..l. ...h h... .h.. ..h. .h.l l.h. .l.h h.l. ";

    /// <summary>One atlas cell, in device pixels from its top-left corner.</summary>
    private sealed class Cell(CanvasDrawingSession ds, float ox, float oy, float w, float h, float light)
    {
        private static readonly Color White = Colors.White;

        private static float Middle(float extent, float thick) => MathF.Max(0, MathF.Floor((extent - thick) / 2));

        private void Rect(float x0, float y0, float x1, float y1, float alpha = 1f)
        {
            if (x1 <= x0 || y1 <= y0) return;
            ds.Antialiasing = CanvasAntialiasing.Aliased;
            ds.FillRectangle(ox + x0, oy + y0, x1 - x0, y1 - y0, alpha >= 1f ? White : Color.FromArgb((byte)(alpha * 255), 255, 255, 255));
        }

        public void Box(int cp)
        {
            switch (cp)
            {
                case 0x2504: Dashes(false, false, 3, DashGap.Four); return;
                case 0x2505: Dashes(false, true, 3, DashGap.Four); return;
                case 0x2506: Dashes(true, false, 3, DashGap.Four); return;
                case 0x2507: Dashes(true, true, 3, DashGap.Four); return;
                case 0x2508: Dashes(false, false, 4, DashGap.Four); return;
                case 0x2509: Dashes(false, true, 4, DashGap.Four); return;
                case 0x250A: Dashes(true, false, 4, DashGap.Four); return;
                case 0x250B: Dashes(true, true, 4, DashGap.Four); return;
                case 0x254C: Dashes(false, false, 2, DashGap.Light); return;
                case 0x254D: Dashes(false, true, 2, DashGap.Heavy); return;
                case 0x254E: Dashes(true, false, 2, DashGap.Heavy); return;
                case 0x254F: Dashes(true, true, 2, DashGap.Heavy); return;
                case 0x256D: Arc(1, 1); return;
                case 0x256E: Arc(-1, 1); return;
                case 0x256F: Arc(-1, -1); return;
                case 0x2570: Arc(1, -1); return;
                case 0x2571: Diagonal(true, false); return;
                case 0x2572: Diagonal(false, true); return;
                case 0x2573: Diagonal(true, true); return;
            }
            int at = (cp - 0x2500) * 5;
            static Weight Of(char c) => c switch { 'l' => Weight.Light, 'h' => Weight.Heavy, 'd' => Weight.Double, _ => Weight.None };
            Lines(Of(BoxLines[at]), Of(BoxLines[at + 1]), Of(BoxLines[at + 2]), Of(BoxLines[at + 3]));
        }

        /// <summary>Ghostty's linesChar: each line runs from its edge to where it meets the others.</summary>
        private void Lines(Weight up, Weight right, Weight down, Weight left)
        {
            const Weight None = Weight.None, Light = Weight.Light, Heavy = Weight.Heavy, Double = Weight.Double;
            float heavy = 2 * light;
            float hLightTop = Middle(h, light), hLightBottom = hLightTop + light;
            float hHeavyTop = Middle(h, heavy), hHeavyBottom = hHeavyTop + heavy;
            float hDoubleTop = MathF.Max(0, hLightTop - light), hDoubleBottom = hLightBottom + light;
            float vLightLeft = Middle(w, light), vLightRight = vLightLeft + light;
            float vHeavyLeft = Middle(w, heavy), vHeavyRight = vHeavyLeft + heavy;
            float vDoubleLeft = MathF.Max(0, vLightLeft - light), vDoubleRight = vLightRight + light;

            float upBottom = left == Heavy || right == Heavy ? hHeavyBottom
                : left != right || down == up ? (left == Double || right == Double ? hDoubleBottom : hLightBottom)
                : left == None && right == None ? hLightBottom : hLightTop;
            float downTop = left == Heavy || right == Heavy ? hHeavyTop
                : left != right || up == down ? (left == Double || right == Double ? hDoubleTop : hLightTop)
                : left == None && right == None ? hLightTop : hLightBottom;
            float leftRight = up == Heavy || down == Heavy ? vHeavyRight
                : up != down || left == right ? (up == Double || down == Double ? vDoubleRight : vLightRight)
                : up == None && down == None ? vLightRight : vLightLeft;
            float rightLeft = up == Heavy || down == Heavy ? vHeavyLeft
                : up != down || right == left ? (up == Double || down == Double ? vDoubleLeft : vLightLeft)
                : up == None && down == None ? vLightLeft : vLightRight;

            switch (up)
            {
                case Light: Rect(vLightLeft, 0, vLightRight, upBottom); break;
                case Heavy: Rect(vHeavyLeft, 0, vHeavyRight, upBottom); break;
                case Double:
                    Rect(vDoubleLeft, 0, vLightLeft, left == Double ? hLightTop : upBottom);
                    Rect(vLightRight, 0, vDoubleRight, right == Double ? hLightTop : upBottom);
                    break;
            }
            switch (right)
            {
                case Light: Rect(rightLeft, hLightTop, w, hLightBottom); break;
                case Heavy: Rect(rightLeft, hHeavyTop, w, hHeavyBottom); break;
                case Double:
                    Rect(up == Double ? vLightRight : rightLeft, hDoubleTop, w, hLightTop);
                    Rect(down == Double ? vLightRight : rightLeft, hLightBottom, w, hDoubleBottom);
                    break;
            }
            switch (down)
            {
                case Light: Rect(vLightLeft, downTop, vLightRight, h); break;
                case Heavy: Rect(vHeavyLeft, downTop, vHeavyRight, h); break;
                case Double:
                    Rect(vDoubleLeft, left == Double ? hLightBottom : downTop, vLightLeft, h);
                    Rect(vLightRight, right == Double ? hLightBottom : downTop, vDoubleRight, h);
                    break;
            }
            switch (left)
            {
                case Light: Rect(0, hLightTop, leftRight, hLightBottom); break;
                case Heavy: Rect(0, hHeavyTop, leftRight, hHeavyBottom); break;
                case Double:
                    Rect(0, hDoubleTop, up == Double ? vLightLeft : leftRight, hLightTop);
                    Rect(0, hLightBottom, down == Double ? vLightLeft : leftRight, hDoubleBottom);
                    break;
            }
        }

        /// <summary>Dashes that tile into an even line: half gaps at both ends of a horizontal cell, a whole gap at the foot of a vertical one.</summary>
        private void Dashes(bool vertical, bool heavyLine, int count, DashGap gapKind)
        {
            float thick = heavyLine ? 2 * light : light;
            float desired = gapKind switch { DashGap.Four => MathF.Max(light, 4), DashGap.Light => light, _ => 2 * light };
            float along = vertical ? h : w, across = vertical ? w : h;
            float side = Middle(across, thick);
            float span = MathF.Floor(along);
            if (span < 2 * count)
            {
                if (vertical) { float x = Middle(w, light); Rect(x, 0, x + light, h); }
                else { float y = Middle(h, light); Rect(0, y, w, y + light); }
                return;
            }
            float gap = MathF.Min(desired, MathF.Floor(span / (2 * count)));
            float total = span - count * gap;
            float dash = MathF.Floor(total / count);
            float extra = total - dash * count;
            float at = vertical ? 0 : MathF.Floor(gap / 2);
            for (int i = 0; i < count; i++)
            {
                float end = at + dash;
                if (extra > 0) { extra--; end++; }
                if (vertical) Rect(side, at, side + thick, end);
                else Rect(at, side, end, side + thick);
                at = end + gap;
            }
        }

        /// <summary>A rounded corner from the middle of one edge to the middle of another; its straight ends are redrawn as whole-pixel lines.</summary>
        private void Arc(float dx, float dy)
        {
            float left = Middle(w, light), top = Middle(h, light);
            float cx = left + light / 2, cy = top + light / 2;
            float r = MathF.Min(MathF.Min(w, h) / 2, MathF.Min(dx > 0 ? w - cx : cx, dy > 0 ? h - cy : cy));
            const float s = 0.25f;
            using (var path = new CanvasPathBuilder(ds))
            {
                path.BeginFigure(ox + cx, oy + cy + dy * r);
                path.AddCubicBezier(new Vector2(ox + cx, oy + cy + dy * s * r), new Vector2(ox + cx + dx * s * r, oy + cy), new Vector2(ox + cx + dx * r, oy + cy));
                path.EndFigure(CanvasFigureLoop.Open);
                using var geometry = CanvasGeometry.CreatePath(path);
                ds.Antialiasing = CanvasAntialiasing.Antialiased;
                ds.DrawGeometry(geometry, White, light);
            }
            float x = MathF.Round(cx + dx * 0.8f * r), y = MathF.Round(cy + dy * 0.8f * r);
            if (dy > 0) Rect(left, y, left + light, h); else Rect(left, 0, left + light, y);
            if (dx > 0) Rect(x, top, w, top + light); else Rect(0, top, x, top + light);
        }

        /// <summary>Corner-to-corner lines that overshoot slightly along their slope so neighbouring diagonals join.</summary>
        private void Diagonal(bool rising, bool falling)
        {
            float sx = MathF.Min(w / h, 1) * 0.5f, sy = MathF.Min(h / w, 1) * 0.5f;
            ds.Antialiasing = CanvasAntialiasing.Antialiased;
            using var style = new CanvasStrokeStyle { StartCap = CanvasCapStyle.Flat, EndCap = CanvasCapStyle.Flat };
            if (rising) ds.DrawLine(ox + w + sx, oy - sy, ox - sx, oy + h + sy, White, light, style);
            if (falling) ds.DrawLine(ox - sx, oy - sy, ox + w + sx, oy + h + sy, White, light, style);
        }

        /// <summary>Block elements: edges at eighths and halves rounded to whole pixels; shades as translucent fills.</summary>
        public void Block(int cp)
        {
            float X(float share) => MathF.Round(w * share);
            float Y(float share) => MathF.Round(h * share);
            switch (cp)
            {
                case 0x2580: Rect(0, 0, w, Y(0.5f)); return;
                case >= 0x2581 and <= 0x2587: Rect(0, Y(1 - (cp - 0x2580) / 8f), w, h); return;
                case 0x2588: Rect(0, 0, w, h); return;
                case >= 0x2589 and <= 0x258F: Rect(0, 0, X((0x2590 - cp) / 8f), h); return;
                case 0x2590: Rect(X(0.5f), 0, w, h); return;
                case 0x2591: Rect(0, 0, w, h, 0.25f); return;
                case 0x2592: Rect(0, 0, w, h, 0.5f); return;
                case 0x2593: Rect(0, 0, w, h, 0.75f); return;
                case 0x2594: Rect(0, 0, w, Y(1 / 8f)); return;
                case 0x2595: Rect(X(7 / 8f), 0, w, h); return;
            }
            // Quadrants: bit 0 upper left, 1 upper right, 2 lower left, 3 lower right.
            int bits = cp switch
            {
                0x2596 => 0b0100, 0x2597 => 0b1000, 0x2598 => 0b0001, 0x2599 => 0b1101, 0x259A => 0b1001,
                0x259B => 0b0111, 0x259C => 0b1011, 0x259D => 0b0010, 0x259E => 0b0110, 0x259F => 0b1110, _ => 0,
            };
            float mx = X(0.5f), my = Y(0.5f);
            if ((bits & 1) != 0) Rect(0, 0, mx, my);
            if ((bits & 2) != 0) Rect(mx, 0, w, my);
            if ((bits & 4) != 0) Rect(0, my, mx, h);
            if ((bits & 8) != 0) Rect(mx, my, w, h);
        }

        /// <summary>Powerline separators: triangles, half circles and slashes filling the cell edge to edge.</summary>
        public void Powerline(int cp)
        {
            ds.Antialiasing = CanvasAntialiasing.Antialiased;
            Vector2 P(float x, float y) => new(ox + x, oy + y);
            switch (cp)
            {
                case 0xE0B0: Fill(P(0, 0), P(w, h / 2), P(0, h)); break;
                case 0xE0B1: Stroke(P(0, 0), P(w - light / 2, h / 2), P(0, h)); break;
                case 0xE0B2: Fill(P(w, 0), P(0, h / 2), P(w, h)); break;
                case 0xE0B3: Stroke(P(w, 0), P(light / 2, h / 2), P(w, h)); break;
                case 0xE0B4: ds.FillEllipse(P(0, h / 2), w, h / 2, White); break;
                case 0xE0B5: ds.DrawEllipse(P(0, h / 2), w - light / 2, h / 2 - light / 2, White, light); break;
                case 0xE0B6: ds.FillEllipse(P(w, h / 2), w, h / 2, White); break;
                case 0xE0B7: ds.DrawEllipse(P(w, h / 2), w - light / 2, h / 2 - light / 2, White, light); break;
                case 0xE0B8: Fill(P(0, 0), P(w, h), P(0, h)); break;
                case 0xE0BA: Fill(P(w, 0), P(w, h), P(0, h)); break;
                case 0xE0BC: Fill(P(0, 0), P(w, 0), P(0, h)); break;
                case 0xE0BE: Fill(P(0, 0), P(w, 0), P(w, h)); break;
                case 0xE0B9 or 0xE0BF: ds.DrawLine(P(0, 0), P(w, h), White, light); break;
                case 0xE0BB or 0xE0BD: ds.DrawLine(P(w, 0), P(0, h), White, light); break;
            }
        }

        private void Fill(params Vector2[] points)
        {
            using var polygon = CanvasGeometry.CreatePolygon(ds, points);
            ds.FillGeometry(polygon, White);
        }

        private void Stroke(params Vector2[] points)
        {
            using var path = new CanvasPathBuilder(ds);
            path.BeginFigure(points[0]);
            for (int i = 1; i < points.Length; i++) path.AddLine(points[i]);
            path.EndFigure(CanvasFigureLoop.Open);
            using var geometry = CanvasGeometry.CreatePath(path);
            using var style = new CanvasStrokeStyle { LineJoin = CanvasLineJoin.Miter };
            ds.DrawGeometry(geometry, White, light, style);
        }
    }
}
