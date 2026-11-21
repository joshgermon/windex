// The focused card's lift (design §5.1: Z 8 with a soft shadow). An in-tree ThemeShadow does not
// draw on the window backdrop, so the card casts its own composition shadow: a brushless sprite
// whose DropShadow is masked to the card's rounded rectangle and clipped to the outside of the
// card, so the translucent card surface never shows the shadow through it.
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Windex.Panes;

internal sealed class CardShadow
{
    private const float Blur = 24;
    private const float OffsetY = 6;
    /// <summary>How far the clip reaches past the card: enough for the blur and offset.</summary>
    private const float Reach = 48;

    private readonly Compositor _compositor;
    private readonly SpriteVisual _visual;
    private readonly ShapeVisual _shapes;
    private readonly CompositionRoundedRectangleGeometry _caster;
    private readonly CompositionVisualSurface _surface;
    private readonly DropShadow _shadow;
    private readonly ScalarKeyFrameAnimation _fadeAnimation;
    private readonly ImplicitAnimationCollection _fade;
    private readonly float _radius;
    private Vector2 _size;

    public CardShadow(UIElement host, float radius)
    {
        _radius = radius;
        _compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;

        // The mask: the card's rounded rectangle, rendered into a surface.
        _caster = _compositor.CreateRoundedRectangleGeometry();
        _caster.CornerRadius = new Vector2(radius);
        var shape = _compositor.CreateSpriteShape(_caster);
        shape.FillBrush = _compositor.CreateColorBrush(Colors.Black);
        _shapes = _compositor.CreateShapeVisual();
        _shapes.Shapes.Add(shape);
        _surface = _compositor.CreateVisualSurface();
        _surface.SourceVisual = _shapes;

        _shadow = _compositor.CreateDropShadow();
        _shadow.BlurRadius = Blur;
        _shadow.Offset = new Vector3(0, OffsetY, 0);
        _shadow.Color = Colors.Black;
        _shadow.Mask = _compositor.CreateSurfaceBrush(_surface);

        // No brush: the sprite draws nothing but its shadow.
        _visual = _compositor.CreateSpriteVisual();
        _visual.Shadow = _shadow;
        _visual.Opacity = 0;

        _fadeAnimation = _compositor.CreateScalarKeyFrameAnimation();
        _fadeAnimation.InsertExpressionKeyFrame(1f, "this.FinalValue");
        _fadeAnimation.Duration = Services.Motion.Duration(Services.Motion.Fast);
        _fadeAnimation.Target = "Opacity";
        _fade = _compositor.CreateImplicitAnimationCollection();
        _fade["Opacity"] = _fadeAnimation;

        ElementCompositionPreview.SetElementChildVisual(host, _visual);
    }

    /// <summary>Follow the card's size: the mask and the clip that keeps the shadow outside the card.</summary>
    public void Resize(Vector2 size)
    {
        if (size == _size) return;
        _size = size;
        _visual.Size = _shapes.Size = _caster.Size = _surface.SourceSize = size;
        var device = CanvasDevice.GetSharedDevice();
        using var outer = CanvasGeometry.CreateRectangle(device, -Reach, -Reach, size.X + 2 * Reach, size.Y + 2 * Reach);
        using var card = CanvasGeometry.CreateRoundedRectangle(device, 0, 0, size.X, size.Y, _radius, _radius);
        var outside = outer.CombineWith(card, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
        _visual.Clip = _compositor.CreateGeometricClip(_compositor.CreatePathGeometry(new CompositionPath(outside)));
    }

    /// <summary>Lift (or lower) the card; a dark ground needs a denser shadow to read.</summary>
    public void Show(bool lifted, bool dark, bool animate)
    {
        _shadow.Opacity = dark ? 0.5f : 0.16f;
        _fadeAnimation.Duration = Services.Motion.Duration(Services.Motion.Fast);
        _visual.ImplicitAnimations = animate ? _fade : null;
        _visual.Opacity = lifted ? 1 : 0;
    }
}
