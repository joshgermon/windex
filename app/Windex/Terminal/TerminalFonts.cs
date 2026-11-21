// The terminal's font stack at one size and scale: cell metrics in whole device pixels and a
// cache that resolves each grapheme cluster to something the renderer can pin to its cell.
//
// Resolution order (docs/design.md §7.2): the primary family (Cascadia Mono, else Consolas) →
// Cascadia Mono/Code NF when installed → the bundled Symbols Nerd Font → Segoe UI Symbol →
// Segoe UI Emoji (colour) → DirectWrite's system fallback. Box drawing, block elements and
// Powerline separators never come from a font: they are sprites (TerminalSprites).
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.UI.Text;

namespace Windex.Terminal;

internal enum GlyphKind : byte
{
    /// <summary>Nothing to draw (a space, or a character no font has).</summary>
    None,
    /// <summary>One glyph of a face slot, drawn in the cell's glyph run.</summary>
    Glyph,
    /// <summary>A box-drawing / block / Powerline sprite from the atlas.</summary>
    Sprite,
    /// <summary>Several shaped glyphs (combining marks, system fallback) added to the glyph runs.</summary>
    Composite,
    /// <summary>A colour-font cluster (emoji) drawn as its own text layout.</summary>
    Color,
}

/// <summary>What a cell's text resolved to. Offsets are device pixels from the cell's left edge and baseline.</summary>
internal readonly record struct GlyphEntry(GlyphKind Kind, int Slot, int Index, float Dx, float Dy)
{
    public static readonly GlyphEntry Empty = new(GlyphKind.None, 0, 0, 0, 0);
}

/// <summary>A face at a size: the unit a glyph run is drawn with.</summary>
internal sealed record FaceSlot(CanvasFontFace Face, float Size);

/// <summary>One glyph of a composite cluster.</summary>
internal readonly record struct PlacedGlyph(int Slot, int Index, float Dx, float Dy);

/// <summary>A colour cluster: drawn with DrawTextLayout at (cell x + Dx, cell top + Top).</summary>
internal sealed record ColorGlyph(CanvasTextLayout Layout, float Dx, float Top);

internal sealed class TerminalFonts
{
    private const int MaxCacheEntries = 16384;
    private const int MaxSharedStacks = 8;
    private static readonly Dictionary<(string, float, float, float, CanvasDevice), TerminalFonts> Shared = new();

    public CanvasDevice Device { get; }
    public string Family { get; }
    public float Scale { get; }
    /// <summary>Font size in device pixels.</summary>
    public float SizePx { get; }
    public int CellWidth { get; }
    public int CellHeight { get; }
    /// <summary>Baseline offset from the cell top, whole pixels.</summary>
    public int Baseline { get; }
    public int UnderlineTop { get; }
    public int UnderlineThickness { get; }
    public int StrikeTop { get; }
    public int StrikeThickness { get; }
    /// <summary>A light box-drawing line: one effective pixel, in whole device pixels.</summary>
    public int LightLine { get; }

    /// <summary>Face slots 0–3 are the primary family's regular, bold, italic and bold italic.</summary>
    public List<FaceSlot> Slots { get; } = new();
    public List<PlacedGlyph[]> Composites { get; } = new();
    public List<ColorGlyph> Colors { get; } = new();

    private readonly CanvasFontFace[] _fallbackFaces;
    private readonly CanvasFontFace? _symbolFace;
    private readonly CanvasFontFace? _emojiFace;
    private readonly GlyphEntry[][] _ascii = new GlyphEntry[4][];
    private readonly Dictionary<long, GlyphEntry> _single = new();
    private readonly Dictionary<string, GlyphEntry> _clusters = new();
    private readonly Dictionary<string, GlyphEntry>.AlternateLookup<ReadOnlySpan<char>> _clusterLookup;
    private readonly Dictionary<string, int> _slotIndex = new();

    /// <summary>The shared font stack for a family, size (DIPs), line height (× size) and rasterization scale.</summary>
    public static TerminalFonts Get(CanvasDevice device, string family, float sizeDip, float lineHeight, float scale)
    {
        var key = (family, sizeDip, lineHeight, scale, device);
        if (!Shared.TryGetValue(key, out var fonts))
        {
            // Drop stacks of a lost device, and old sizes once there are several (font size changes);
            // views keep the instance they hold until they next re-measure.
            foreach (var stale in Shared.Keys.Where(k => k.Item5 != device).ToList()) Shared.Remove(stale);
            if (Shared.Count >= MaxSharedStacks) Shared.Clear();
            Shared[key] = fonts = new TerminalFonts(device, family, sizeDip, lineHeight, scale);
        }
        return fonts;
    }

    private TerminalFonts(CanvasDevice device, string family, float sizeDip, float lineHeight, float scale)
    {
        Device = device;
        Scale = scale;
        SizePx = sizeDip * scale;
        _clusterLookup = _clusters.GetAlternateLookup<ReadOnlySpan<char>>();

        var system = CanvasFontSet.GetSystemFontSet();
        string primary = new[] { family, "Cascadia Mono", "Consolas" }.First(f => Find(system, f, 400, FontStyle.Normal) is not null);
        Family = primary;
        foreach (var (weight, style) in new[] { (400, FontStyle.Normal), (700, FontStyle.Normal), (400, FontStyle.Italic), (700, FontStyle.Italic) })
            AddSlot(Find(system, primary, weight, style)!, SizePx);

        var regular = Slots[0].Face;
        var m = regular.GetGlyphMetrics(regular.GetGlyphIndices(new uint[] { 'M' }), false)[0];
        CellWidth = Math.Max(1, (int)MathF.Round(m.AdvanceWidth * SizePx));
        CellHeight = Math.Max(1, (int)MathF.Ceiling(SizePx * lineHeight - 0.001f));
        float ascent = regular.Ascent * SizePx, descent = regular.Descent * SizePx;
        Baseline = (int)MathF.Round((CellHeight - ascent - descent) / 2 + ascent);
        UnderlineThickness = Math.Max(1, (int)MathF.Round(regular.UnderlineThickness * SizePx));
        UnderlineTop = Math.Min(CellHeight - UnderlineThickness, Baseline + Math.Max(1, (int)MathF.Round(-regular.UnderlinePosition * SizePx)));
        StrikeThickness = UnderlineThickness;
        StrikeTop = Baseline - (int)MathF.Round(regular.StrikethroughPosition * SizePx + StrikeThickness / 2f);
        LightLine = Math.Max(1, (int)MathF.Round(scale));

        var fallbacks = new List<CanvasFontFace>();
        foreach (var nf in new[] { "Cascadia Mono NF", "Cascadia Code NF" })
            if (!string.Equals(nf, primary, StringComparison.OrdinalIgnoreCase) && Find(system, nf, 400, FontStyle.Normal) is { } f)
                fallbacks.Add(f);
        if (LoadBundledSymbols() is { } symbols) fallbacks.Add(symbols);
        _fallbackFaces = fallbacks.ToArray();
        _symbolFace = Find(system, "Segoe UI Symbol", 400, FontStyle.Normal);
        _emojiFace = Find(system, "Segoe UI Emoji", 400, FontStyle.Normal);

        for (int s = 0; s < 4; s++)
        {
            var table = _ascii[s] = new GlyphEntry[128];
            for (int ch = 0x21; ch < 0x7F; ch++)
                table[ch] = ResolveSingle(ch, s, wide: false, nextBlank: false);
        }
    }

    private static CanvasFontFace? Find(CanvasFontSet set, string family, int weight, FontStyle style)
    {
        var fonts = set.GetMatchingFonts(family, new FontWeight((ushort)weight), FontStretch.Normal, style).Fonts;
        foreach (var face in fonts)
            if (face.FamilyNames.Values.Any(n => string.Equals(n, family, StringComparison.OrdinalIgnoreCase)))
                return face;
        return null;
    }

    private static CanvasFontFace? LoadBundledSymbols()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "SymbolsNerdFont-Regular.ttf");
        if (!File.Exists(path)) return null;
        try
        {
            var set = new CanvasFontSet(new Uri(path));
            return set.Fonts.Count > 0 ? set.Fonts[0] : null;
        }
        catch (Exception ex) when (ex is ArgumentException or COMException)
        {
            return null;
        }
    }

    private int AddSlot(CanvasFontFace face, float size)
    {
        var key = SlotKey(face, size);
        if (_slotIndex.TryGetValue(key, out var index)) return index;
        Slots.Add(new FaceSlot(face, size));
        return _slotIndex[key] = Slots.Count - 1;
    }

    private static string SlotKey(CanvasFontFace face, float size) =>
        $"{face.FamilyNames.Values.FirstOrDefault()}|{face.Weight.Weight}|{face.Style}|{face.Stretch}|{face.Simulations}|{size:0.###}";

    // ------------------------------------------------------------------ lookup (hot path)

    /// <summary>
    /// Resolves a cell's grapheme cluster. <paramref name="style"/>: bit 0 bold, bit 1 italic.
    /// <paramref name="nextBlank"/> lets a symbol-font icon spill into a following blank cell.
    /// </summary>
    public GlyphEntry Lookup(ReadOnlySpan<char> text, int style, bool wide, bool nextBlank)
    {
        if (text.Length == 1)
        {
            char ch = text[0];
            if (ch < 0x80 && !wide) return ch > 0x20 && ch < 0x7F ? _ascii[style][ch] : GlyphEntry.Empty;
            return Single(ch, style, wide, nextBlank);
        }
        if (text.Length == 2 && char.IsSurrogatePair(text[0], text[1]))
            return Single(char.ConvertToUtf32(text[0], text[1]), style, wide, nextBlank);

        Span<char> key = text.Length < 62 ? stackalloc char[text.Length + 1] : new char[text.Length + 1];
        key[0] = (char)('0' + style + (wide ? 4 : 0));
        text.CopyTo(key[1..]);
        if (_clusterLookup.TryGetValue(key, out var entry)) return entry;
        entry = ResolveCluster(text.ToString(), style, wide);
        _clusters[key.ToString()] = entry;
        return entry;
    }

    private GlyphEntry Single(int cp, int style, bool wide, bool nextBlank)
    {
        long key = ((long)cp << 4) | (long)style | (wide ? 4L : 0) | (nextBlank ? 8L : 0);
        if (_single.TryGetValue(key, out var entry)) return entry;
        entry = ResolveSingle(cp, style, wide, nextBlank);
        _single[key] = entry;
        return entry;
    }

    /// <summary>
    /// Called before a frame is prepared: a cache that has grown past its limit starts over. Entries
    /// refer to composites and colour layouts by index, so those go too, and only between frames.
    /// </summary>
    public void BeginFrame()
    {
        if (_single.Count + _clusters.Count < MaxCacheEntries) return;
        _single.Clear();
        _clusters.Clear();
        Composites.Clear();
        foreach (var glyph in Colors) glyph.Layout.Dispose();
        Colors.Clear();
    }

    // ------------------------------------------------------------------ resolution

    private GlyphEntry ResolveSingle(int cp, int style, bool wide, bool nextBlank)
    {
        if (cp <= 0x20 || cp == 0x7F || cp == 0xA0) return GlyphEntry.Empty;
        if (TerminalSprites.Has(cp)) return new GlyphEntry(GlyphKind.Sprite, 0, TerminalSprites.IndexOf(cp), 0, 0);

        int span = wide ? 2 : 1;
        if (wide && PrefersEmoji(cp) && _emojiFace?.HasCharacter((uint)cp) == true)
            return ResolveCluster(char.ConvertFromUtf32(cp), style, wide);

        var primary = Slots[style].Face;
        if (primary.HasCharacter((uint)cp))
            return PlaceGlyph(primary, style, cp, span, pinLeft: false, nextBlank: false);

        foreach (var face in _fallbackFaces)
            if (face.HasCharacter((uint)cp))
                return PlaceGlyph(face, -1, cp, span, pinLeft: IsPrivateUse(cp), nextBlank);

        if (_symbolFace?.HasCharacter((uint)cp) == true)
            return PlaceGlyph(_symbolFace, -1, cp, span, pinLeft: false, nextBlank: false);

        return ResolveCluster(char.ConvertFromUtf32(cp), style, wide);
    }

    /// <summary>
    /// One glyph pinned to its cell(s). Centred when narrower than its cells; a symbol-font icon
    /// (Private Use Area) starts at the cell's left edge as Rex draws it and may use a following
    /// blank cell; anything wider than the room it has is scaled down to fit.
    /// </summary>
    private GlyphEntry PlaceGlyph(CanvasFontFace face, int primarySlot, int cp, int span, bool pinLeft, bool nextBlank)
    {
        int index = face.GetGlyphIndices(new[] { (uint)cp })[0];
        var metrics = face.GetGlyphMetrics(new[] { index }, false)[0];
        float advance = metrics.AdvanceWidth * SizePx;
        float room = CellWidth * span;
        if (pinLeft)
        {
            float inkRight = (float)(metrics.DrawBounds.X + metrics.DrawBounds.Width) * SizePx;
            float limit = room + (nextBlank ? CellWidth : 0);
            float scale = inkRight > limit + 0.5f ? limit / inkRight : 1f;
            int slot = primarySlot >= 0 && scale == 1f ? primarySlot : AddSlot(face, SizePx * scale);
            return new GlyphEntry(GlyphKind.Glyph, slot, index, 0, 0);
        }
        else
        {
            float scale = advance > room + 0.5f ? room / advance : 1f;
            int slot = primarySlot >= 0 && scale == 1f ? primarySlot : AddSlot(face, SizePx * scale);
            return new GlyphEntry(GlyphKind.Glyph, slot, index, (room - advance * scale) / 2, 0);
        }
    }

    /// <summary>Shapes a cluster with DirectWrite (system fallback included) and keeps what it drew.</summary>
    private GlyphEntry ResolveCluster(string text, int style, bool wide)
    {
        bool emoji = IsEmojiSequence(text) || (wide && text.Length <= 2 && PrefersEmoji(char.ConvertToUtf32(text, 0)));
        var weight = new FontWeight((ushort)((style & 1) != 0 ? 700 : 400));
        var fontStyle = (style & 2) != 0 ? FontStyle.Italic : FontStyle.Normal;
        using var format = new CanvasTextFormat
        {
            FontFamily = emoji ? "Segoe UI Emoji" : Family,
            FontSize = SizePx,
            FontWeight = weight,
            FontStyle = fontStyle,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };
        using var layout = new CanvasTextLayout(Device, text, format, 0, 0);
        var capture = new GlyphCapture();
        layout.DrawToTextRenderer(capture, 0, 0);
        if (capture.Glyphs.Count == 0) return GlyphEntry.Empty;

        float width = (float)layout.LayoutBounds.Width;
        float room = CellWidth * (wide ? 2 : 1);
        float scale = width > room + 0.5f ? room / width : 1f;
        float dx = (room - width * scale) / 2;

        if (capture.Glyphs.Any(g => IsColorFace(g.Face)))
        {
            // Emoji: the picture (its ink, not its advance) fills the cells, centred both ways.
            var ink = layout.DrawBounds;
            float fit = ink.Width > 0 && ink.Height > 0
                ? MathF.Min((room - 1) / (float)ink.Width, (CellHeight - 1) / (float)ink.Height)
                : scale;
            format.FontSize = SizePx * fit;
            format.Options = CanvasDrawTextOptions.EnableColorFont;
            var colored = new CanvasTextLayout(Device, text, format, 0, 0) { Options = CanvasDrawTextOptions.EnableColorFont };
            var box = colored.DrawBounds;
            Colors.Add(new ColorGlyph(colored, (float)((room - box.Width) / 2 - box.X), (float)((CellHeight - box.Height) / 2 - box.Y)));
            return new GlyphEntry(GlyphKind.Color, 0, Colors.Count - 1, 0, 0);
        }

        var placed = capture.Glyphs
            .Select(g => new PlacedGlyph(AddSlot(g.Face, g.Size * scale), g.Index, dx + g.X * scale, g.Up * scale))
            .ToArray();
        if (placed.Length == 1)
            return new GlyphEntry(GlyphKind.Glyph, placed[0].Slot, placed[0].Index, placed[0].Dx, placed[0].Dy);
        Composites.Add(placed);
        return new GlyphEntry(GlyphKind.Composite, 0, Composites.Count - 1, 0, 0);
    }

    private static bool IsColorFace(CanvasFontFace face) =>
        face.FamilyNames.Values.Any(n => n.Contains("Emoji", StringComparison.OrdinalIgnoreCase));

    private static bool IsPrivateUse(int cp) => cp is >= 0xE000 and <= 0xF8FF or >= 0xF0000;

    /// <summary>A wide symbol outside the CJK blocks is an emoji-presentation character.</summary>
    private static bool PrefersEmoji(int cp) => cp >= 0x2000 && !IsCjk(cp) && !IsPrivateUse(cp);

    private static bool IsCjk(int cp) => cp is >= 0x1100 and <= 0x11FF or >= 0x2E80 and <= 0xA4CF or >= 0xA960 and <= 0xA97F
        or >= 0xAC00 and <= 0xD7AF or >= 0xF900 and <= 0xFAFF or >= 0xFE10 and <= 0xFE6F or >= 0xFF00 and <= 0xFFEF
        or >= 0x1B000 and <= 0x1B2FF or >= 0x20000 and <= 0x3FFFD;

    private static bool IsEmojiSequence(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            int cp = char.ConvertToUtf32(text, i);
            if (cp > 0xFFFF) i++;
            if (cp is 0xFE0F or 0x200D or 0x20E3 or >= 0x1F1E6 and <= 0x1F1FF or >= 0x1F3FB and <= 0x1F3FF or >= 0x1F300 and <= 0x1FAFF)
                return true;
        }
        return false;
    }

    /// <summary>Collects the glyph runs a text layout draws: fonts, glyphs and positions after shaping and fallback.</summary>
    private sealed partial class GlyphCapture : ICanvasTextRenderer
    {
        public readonly record struct Captured(CanvasFontFace Face, float Size, int Index, float X, float Up);
        public List<Captured> Glyphs { get; } = new();
        private float? _baseline;

        public void DrawGlyphRun(Vector2 point, CanvasFontFace fontFace, float fontSize, CanvasGlyph[] glyphs, bool isSideways,
            uint bidiLevel, object brush, CanvasTextMeasuringMode measuringMode, string localeName, string textString,
            int[] clusterMapIndices, uint characterIndex, CanvasGlyphOrientation glyphOrientation)
        {
            _baseline ??= point.Y;
            float x = point.X;
            foreach (var g in glyphs)
            {
                Glyphs.Add(new Captured(fontFace, fontSize, g.Index, x + g.AdvanceOffset, g.AscenderOffset + (_baseline.Value - point.Y)));
                x += g.Advance;
            }
        }

        public void DrawStrikethrough(Vector2 point, float strikethroughWidth, float strikethroughThickness, float strikethroughOffset,
            CanvasTextDirection textDirection, object brush, CanvasTextMeasuringMode textMeasuringMode, string localeName,
            CanvasGlyphOrientation glyphOrientation) { }

        public void DrawUnderline(Vector2 point, float underlineWidth, float underlineThickness, float underlineOffset, float runHeight,
            CanvasTextDirection textDirection, object brush, CanvasTextMeasuringMode textMeasuringMode, string localeName,
            CanvasGlyphOrientation glyphOrientation) { }

        public void DrawInlineObject(Vector2 point, ICanvasTextInlineObject inlineObject, bool isSideways, bool isRightToLeft,
            object brush, CanvasGlyphOrientation glyphOrientation) { }

        public float Dpi => 96;
        public bool PixelSnappingDisabled => true;
        public Matrix3x2 Transform => Matrix3x2.Identity;
    }
}
