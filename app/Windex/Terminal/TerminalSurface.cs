// One terminal's drawing surface: a composition drawing surface (premultiplied alpha) shown 1:1 in
// device pixels by a sprite visual hosted in a XAML element. Its transparent pixels blend with
// whatever XAML is behind the host (the translucent, rounded pane card), and XAML elements after
// the host (scrollbar, IME box, paused strip) draw above it.
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.Graphics.DirectX;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;

namespace Windex.Terminal;

internal sealed class TerminalSurface(UIElement host) : IDisposable
{
    private SpriteVisual? _visual;
    private CompositionSurfaceBrush? _brush;
    private CompositionDrawingSurface? _surface;
    private (int Width, int Height) _size;

    public bool HasSurface => _surface is not null;

    /// <summary>Makes the surface <paramref name="width"/>×<paramref name="height"/> device pixels, shown
    /// one surface pixel per device pixel at <paramref name="scale"/>. Its contents are undefined until drawn.</summary>
    public void Resize(int width, int height, float scale)
    {
        if (_surface is null)
        {
            var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
            _surface = TerminalGraphics.GraphicsDevice(compositor).CreateDrawingSurface(
                new Size(width, height), DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);
            _brush = compositor.CreateSurfaceBrush(_surface);
            _brush.Stretch = CompositionStretch.Fill;
            _brush.BitmapInterpolationMode = CompositionBitmapInterpolationMode.NearestNeighbor;
            _visual = compositor.CreateSpriteVisual();
            _visual.Brush = _brush;
            ElementCompositionPreview.SetElementChildVisual(host, _visual);
        }
        else if (_size != (width, height))
        {
            CanvasComposition.Resize(_surface, new Size(width, height));
        }
        _size = (width, height);
        _visual!.Size = new Vector2(width / scale, height / scale);
    }

    /// <summary>A session over the whole surface in device pixels, cleared to transparent.</summary>
    public CanvasDrawingSession CreateDrawingSession()
    {
        var ds = CanvasComposition.CreateDrawingSession(_surface ?? throw new InvalidOperationException("The surface has no size yet."));
        ds.Clear(Colors.Transparent);
        return ds;
    }

    /// <summary>Gives the GPU surface back (the view left the tree); the next Resize makes a new one.</summary>
    public void Dispose()
    {
        if (_visual is null) return;
        ElementCompositionPreview.SetElementChildVisual(host, null);
        _visual.Dispose();
        _brush!.Dispose();
        _surface!.Dispose();
        _visual = null;
        _brush = null;
        _surface = null;
        _size = default;
    }
}
