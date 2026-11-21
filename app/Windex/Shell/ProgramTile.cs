// ProgramTile: a program's icon as a coloured glass tile (tabs, pane headers, sidebar, palette,
// activity), with an optional status badge (design §8.2, §8.5). Corner radius and glyph size
// scale with the tile, so every size keeps the same proportions.
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Windex.Shell;

public sealed class ProgramTile : Grid
{
    public static readonly DependencyProperty IconKeyProperty = Register(nameof(IconKey), typeof(string), null);
    public static readonly DependencyProperty ProgramProperty = Register(nameof(Program), typeof(string), null);
    public static readonly DependencyProperty TileSizeProperty = Register(nameof(TileSize), typeof(double), 16.0);
    public static readonly DependencyProperty BadgeProperty = Register(nameof(Badge), typeof(Tone), Tone.None);
    public static readonly DependencyProperty MonochromeProperty = Register(nameof(Monochrome), typeof(bool), false);

    private static DependencyProperty Register(string name, Type type, object? fallback) =>
        DependencyProperty.Register(name, type, typeof(ProgramTile), new PropertyMetadata(fallback, (d, _) => ((ProgramTile)d).Rebuild()));

    /// <summary>Icon key from the model (pane.icon) or any alias.</summary>
    public string? IconKey { get => (string?)GetValue(IconKeyProperty); set => SetValue(IconKeyProperty, value); }
    /// <summary>Foreground program name, used when the key is generic.</summary>
    public string? Program { get => (string?)GetValue(ProgramProperty); set => SetValue(ProgramProperty, value); }
    /// <summary>Edge length in epx (14–20 in the shell, larger in Settings' icon sheet).</summary>
    public double TileSize { get => (double)GetValue(TileSizeProperty); set => SetValue(TileSizeProperty, value); }
    public Tone Badge { get => (Tone)GetValue(BadgeProperty); set => SetValue(BadgeProperty, value); }
    /// <summary>Colourful icons off: a neutral tile with a secondary glyph.</summary>
    public bool Monochrome { get => (bool)GetValue(MonochromeProperty); set => SetValue(MonochromeProperty, value); }

    public ProgramTile()
    {
        IsHitTestVisible = false;
        ActualThemeChanged += (_, _) => Rebuild();
        Rebuild();
    }

    private void Rebuild()
    {
        Children.Clear();
        var spec = ProgramIcons.Resolve(IconKey, Program);
        double size = TileSize;
        Width = size; Height = size;

        // Rex Mac's tiles are squircle-ish: about a quarter of the edge.
        double radius = Math.Round(size * 0.27 * 2) / 2;
        Brush fill = Monochrome
            ? ThemeLookup.Brush(this, "RexControlFillBrush")!
            : new LinearGradientBrush
            {
                StartPoint = new(0, 0), EndPoint = new(0, 1),
                GradientStops = { new GradientStop { Color = spec.HueLight, Offset = 0 }, new GradientStop { Color = spec.Hue, Offset = 1 } },
            };
        var tile = new Border
        {
            CornerRadius = new CornerRadius(radius),
            Background = fill,
            BorderThickness = new Thickness(Monochrome ? 1 : 0),
            BorderBrush = Monochrome ? ThemeLookup.Brush(this, "RexPaneStrokeBrush") : null,
            BackgroundSizing = BackgroundSizing.OuterBorderEdge,
        };
        var inner = new Grid();
        // Glass sheen: a top-half highlight plus a 1 epx inner rim (Rex Mac's frosted chip look).
        // A near-white tile gets a dark rim and a near-black one a brighter rim, so both keep their
        // shape on a pill or card of the same lightness.
        double lum = ColorMath.Luminance(spec.Hue);
        var (rimTop, rimBottom) = lum > 0.6 ? (Color.FromArgb(0x2B, 0, 0, 0), Color.FromArgb(0x42, 0, 0, 0))
            : lum < 0.03 ? (Color.FromArgb(0x66, 255, 255, 255), Color.FromArgb(0x29, 255, 255, 255))
            : (Color.FromArgb(0x4D, 255, 255, 255), Color.FromArgb(0x14, 255, 255, 255));
        if (!Monochrome)
            inner.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(radius),
                BorderThickness = new Thickness(1),
                BorderBrush = new LinearGradientBrush
                {
                    StartPoint = new(0, 0), EndPoint = new(0, 1),
                    GradientStops =
                    {
                        new GradientStop { Color = rimTop, Offset = 0 },
                        new GradientStop { Color = rimBottom, Offset = 1 },
                    },
                },
                Background = new LinearGradientBrush
                {
                    StartPoint = new(0, 0), EndPoint = new(0, 0.55),
                    GradientStops =
                    {
                        new GradientStop { Color = Color.FromArgb(0x33, 255, 255, 255), Offset = 0 },
                        new GradientStop { Color = Color.FromArgb(0, 255, 255, 255), Offset = 1 },
                    },
                },
            });
        Brush glyphBrush = Monochrome ? ThemeLookup.Brush(this, "RexSecondaryTextBrush")! : new SolidColorBrush(spec.Fg);
        double glyphSize = spec.Source switch
        {
            GlyphSource.Monogram => size * 0.54,
            GlyphSource.Nerd => size * 0.70,
            _ => size * 0.64,
        };
        inner.Children.Add(Glyph(spec, glyphBrush, glyphSize));
        tile.Child = inner;
        Children.Add(tile);

        if (Badge != Tone.None) Children.Add(BadgeDot(Badge, size >= 20 ? 12 : 10));
    }

    private static TextBlock Glyph(ProgramIconSpec spec, Brush fg, double fontSize)
    {
        var text = new TextBlock
        {
            Text = spec.Glyph,
            FontFamily = ProgramIcons.FontFor(spec.Source),
            FontSize = fontSize,
            Foreground = fg,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextLineBounds = TextLineBounds.Tight,
            IsTextScaleFactorEnabled = false,
        }.Decorative();
        if (spec.Source == GlyphSource.Monogram)
        {
            text.FontWeight = FontWeights.SemiBold;
            text.CharacterSpacing = -20;
        }
        return text;
    }

    /// <summary>The status badge: a small filled disc with a symbol, ringed in the surface colour.</summary>
    public static FrameworkElement BadgeDot(Tone tone, double size)
    {
        var (fillKey, glyph) = tone switch
        {
            Tone.Caution => ("RexCautionBrush", "\uE171"),     // Important "!"
            Tone.Critical => ("RexCriticalBrush", "\uE711"),   // Cancel "×"
            Tone.Success => ("RexSuccessBrush", "\uE73E"),     // CheckMark
            _ => ("RexAccentBrush", "\uE712"),                 // More "…" (working)
        };
        var dot = new Border
        {
            Width = size, Height = size,
            CornerRadius = new CornerRadius(size / 2),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -3, -3),
            BorderThickness = new Thickness(1.5),
            BackgroundSizing = BackgroundSizing.InnerBorderEdge,
        };
        dot.Loaded += (_, _) => Paint();
        dot.ActualThemeChanged += (_, _) => Paint();
        Paint();
        return dot;

        void Paint()
        {
            dot.Background = ThemeLookup.Brush(dot, fillKey);
            dot.BorderBrush = ThemeLookup.Brush(dot, "RexTileRingBrush");
            dot.Child = new FontIcon
            {
                Glyph = glyph,
                FontSize = size * 0.55,
                FontWeight = FontWeights.Bold,
                Foreground = ThemeLookup.Brush(dot, "RexTextOnAccentBrush"),
                IsTextScaleFactorEnabled = false,
            }.Decorative();
        }
    }
}
