// Lays out a window's server-owned split tree (docs/contract.md "Layout Node") into rectangles:
// children proportional to their weights, gutters between them (8 epx between glass cards, as on
// Rex Mac; a 1 epx hairline in the Compact pane style), ratios clamped to 0.1–0.9 and cards kept
// at least 160 × 72 where the space allows. The client never invents a layout.
using Windex.Core;
using Windows.Foundation;

namespace Windex.Panes;

/// <summary>A draggable boundary: the split's own rectangle and the gutter between its children.</summary>
public sealed record SplitGeometry(string Id, bool Horizontal, Rect Bounds, Rect Gutter, double Ratio);

public sealed class PaneGeometry
{
    public Dictionary<ulong, Rect> Panes { get; } = new();
    public List<SplitGeometry> Splits { get; } = new();

    public const double CardGap = 8, CompactGap = 1;

    /// <summary>The gutter for the current pane style (set by the window when the style changes).</summary>
    public static double Gap { get; set; } = CardGap;

    public const double MinWidth = 160;
    public const double MinHeight = 72;

    public static PaneGeometry Compute(LayoutNode? root, Rect area, ulong? zoomed, IReadOnlyDictionary<string, double> overrides)
    {
        var g = new PaneGeometry();
        if (root is null || area.Width <= 0 || area.Height <= 0) return g;
        if (zoomed is { } z && root.PaneIds().Contains(z)) { g.Panes[z] = area; return g; }
        g.Place(root, area, overrides);
        return g;
    }

    private void Place(LayoutNode node, Rect r, IReadOnlyDictionary<string, double> overrides)
    {
        if (!node.IsSplit) { Panes[node.Pane] = r; return; }
        var children = node.Children!;
        bool horizontal = node.Axis != "column"; // "row": children left → right
        double length = horizontal ? r.Width : r.Height;
        double available = Math.Max(0, length - Gap * (children.Count - 1));
        double min = horizontal ? MinWidth : MinHeight;

        double[] shares;
        if (children.Count == 2)
        {
            double ratio = node.Id is not null && overrides.TryGetValue(node.Id, out var o) ? o : node.Ratio;
            ratio = Math.Clamp(ratio, 0.1, 0.9);
            if (available >= 2 * min) ratio = Math.Clamp(ratio, min / available, 1 - min / available);
            shares = [ratio, 1 - ratio];
        }
        else
        {
            double total = children.Sum(c => Math.Max(0, c.Weight));
            shares = children.Select(c => total > 0 ? Math.Max(0, c.Weight) / total : 1.0 / children.Count).ToArray();
        }

        double offset = horizontal ? r.X : r.Y;
        for (int i = 0; i < children.Count; i++)
        {
            double size = i == children.Count - 1
                ? (horizontal ? r.X + r.Width : r.Y + r.Height) - offset
                : Math.Round(available * shares[i]);
            var childRect = horizontal ? new Rect(offset, r.Y, Math.Max(0, size), r.Height) : new Rect(r.X, offset, r.Width, Math.Max(0, size));
            Place(children[i].Node, childRect, overrides);
            offset += size;
            if (i < children.Count - 1)
            {
                var gutter = horizontal ? new Rect(offset, r.Y, Gap, r.Height) : new Rect(r.X, offset, r.Width, Gap);
                if (children.Count == 2 && node.Id is not null) Splits.Add(new SplitGeometry(node.Id, horizontal, r, gutter, shares[0]));
                offset += Gap;
            }
        }
    }

    /// <summary>The ratio a divider at <paramref name="position"/> (panel coordinates) gives its split.</summary>
    public static double RatioAt(SplitGeometry split, Windows.Foundation.Point position)
    {
        double start = split.Horizontal ? split.Bounds.X : split.Bounds.Y;
        double length = (split.Horizontal ? split.Bounds.Width : split.Bounds.Height) - Gap;
        double at = (split.Horizontal ? position.X : position.Y) - start - Gap / 2;
        return length <= 0 ? 0.5 : Math.Clamp(at / length, 0.1, 0.9);
    }
}
