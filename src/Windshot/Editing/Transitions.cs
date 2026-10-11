using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.Foundation;

namespace Windshot.Editing;

/// <summary>How a layer comes in or goes out in a recording (when its fade length isn't None).</summary>
internal enum Transition
{
    Fade,
    Pop,
    WipeRight,
    WipeLeft,
    WipeFromEdges,
    WipeFromCenter,
    RadialClockwise,
    RadialCounterclockwise,
}

/// <summary>
/// The shapes of the transitions. Each wipe uncovers a region that grows from nothing to the
/// whole layer: coming in, the layer shows inside it; going out, the region grows the same
/// way and the layer shows outside it, so a wipe right clears it left to right.
/// </summary>
internal static class Transitions
{
    public static readonly (Transition Style, string Name)[] All =
    [
        (Transition.Fade, "Fade"),
        (Transition.Pop, "Pop"),
        (Transition.WipeRight, "Wipe right"),
        (Transition.WipeLeft, "Wipe left"),
        (Transition.WipeFromEdges, "Wipe in from edges"),
        (Transition.WipeFromCenter, "Wipe out from center"),
        (Transition.RadialClockwise, "Radial wipe clockwise"),
        (Transition.RadialCounterclockwise, "Radial wipe counterclockwise"),
    ];

    public static bool IsWipe(this Transition style) => style >= Transition.WipeRight;

    /// <summary>Starts and ends gently.</summary>
    public static float Smooth(float t) => t * t * (3 - 2 * t);

    /// <summary>
    /// The mask that shows a layer partway through its wipes, or null when neither wipe is
    /// under way. <paramref name="comeIn"/> runs 0 to 1 as it comes in; <paramref name="goOut"/>
    /// 0 to 1 as it goes. Feathered edges are the sharp mask, blurred.
    /// </summary>
    public static ICanvasImage? WipeMask(LayerContext context, Annotation layer, float comeIn, float goOut)
    {
        bool wipingIn = comeIn < 1 && layer.InStyle.IsWipe();
        bool wipingOut = goOut > 0 && layer.OutStyle.IsWipe();
        if (!wipingIn && !wipingOut)
            return null;

        var bounds = layer.Bounds;
        float feather = layer.FeatheredWipes ? (float)Math.Max(bounds.Width, bounds.Height) * 0.15f + 2 : 0;
        var outer = bounds.Inflate(feather * 2 + 2);
        var device = context.Device;
        var mask = context.NewList();
        using (var s = mask.CreateDrawingSession())
        {
            var shown = CanvasGeometry.CreateRectangle(device, outer);
            if (wipingIn)
                shown = Combine(shown, Region(device, layer.InStyle, bounds, outer, feather, Smooth(comeIn)), CanvasGeometryCombine.Intersect);
            if (wipingOut)
                shown = Combine(shown, Region(device, layer.OutStyle, bounds, outer, feather, Smooth(goOut)), CanvasGeometryCombine.Exclude);
            s.FillGeometry(shown, Microsoft.UI.Colors.White);
            shown.Dispose();
        }
        if (feather <= 0)
            return mask;
        return context.Own(new GaussianBlurEffect { Source = mask, BlurAmount = feather / 3, BorderMode = EffectBorderMode.Soft });
    }

    private static CanvasGeometry Combine(CanvasGeometry a, CanvasGeometry b, CanvasGeometryCombine how)
    {
        var result = a.CombineWith(b, Matrix3x2.Identity, how);
        a.Dispose();
        b.Dispose();
        return result;
    }

    /// <summary>
    /// The region a wipe has uncovered at <paramref name="p"/> (0 to 1). It starts a feather's
    /// width outside the layer and ends a feather's width past it, so a soft edge never shows
    /// any of the layer at the start, or hides any at the end.
    /// </summary>
    private static CanvasGeometry Region(ICanvasResourceCreator device, Transition style, Rect b, Rect outer, float f, float p)
    {
        double width = b.Width + 2 * f, height = b.Height + 2 * f;
        double cx = b.X + b.Width / 2, cy = b.Y + b.Height / 2;
        switch (style)
        {
            case Transition.WipeRight:
                return CanvasGeometry.CreateRectangle(device, new Rect(b.Left - f, outer.Top, width * p, outer.Height));
            case Transition.WipeLeft:
                return CanvasGeometry.CreateRectangle(device, new Rect(b.Right + f - width * p, outer.Top, width * p, outer.Height));
            case Transition.WipeFromCenter:
                return CanvasGeometry.CreateRectangle(device, new Rect(cx - width * p / 2, cy - height * p / 2, width * p, height * p));
            case Transition.WipeFromEdges:
            {
                var inner = new Rect(cx - width * (1 - p) / 2, cy - height * (1 - p) / 2, width * (1 - p), height * (1 - p));
                return Combine(CanvasGeometry.CreateRectangle(device, outer), CanvasGeometry.CreateRectangle(device, inner), CanvasGeometryCombine.Exclude);
            }
            default:
            {
                // A pie from twelve o'clock, sweeping round; big enough to reach the corners.
                if (p >= 0.999f)
                    return CanvasGeometry.CreateRectangle(device, outer);
                var center = new Vector2((float)cx, (float)cy);
                float radius = (float)Math.Sqrt(outer.Width * outer.Width + outer.Height * outer.Height) / 2;
                float sweep = MathF.Tau * p * (style == Transition.RadialClockwise ? 1 : -1);
                using var path = new CanvasPathBuilder(device);
                path.BeginFigure(center);
                path.AddLine(center + new Vector2(0, -radius));
                path.AddArc(center, radius, radius, -MathF.PI / 2, sweep);
                path.EndFigure(CanvasFigureLoop.Closed);
                return CanvasGeometry.CreatePath(path);
            }
        }
    }

    /// <summary>A pop's size, coming in (0 to 1): from half size, overshooting a little, then settling.</summary>
    public static float PopIn(float t)
    {
        const float c1 = 1.70158f, c3 = c1 + 1;
        float u = t - 1;
        return 0.5f + 0.5f * (1 + c3 * u * u * u + c1 * u * u);
    }

    /// <summary>A pop's size, going out (0 to 1): a slight swell, then shrinking to half size.</summary>
    public static float PopOut(float t)
    {
        const float c1 = 1.70158f, c3 = c1 + 1;
        return 1 - 0.5f * (c3 * t * t * t - c1 * t * t);
    }
}
