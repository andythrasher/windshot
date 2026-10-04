using Windows.Foundation;

namespace Windshot.Editing;

internal static class RectExtensions
{
    /// <summary>Grows (or shrinks, for negative amounts) a rect on every side. Collapses to empty.</summary>
    public static Rect Inflate(this Rect r, double amount)
    {
        double w = r.Width + amount * 2, h = r.Height + amount * 2;
        return w <= 0 || h <= 0 ? Rect.Empty : new Rect(r.X - amount, r.Y - amount, w, h);
    }

    public static Rect UnionWith(this Rect a, Rect b)
    {
        if (a.IsEmpty) return b;
        if (b.IsEmpty) return a;
        a.Union(b);
        return a;
    }

    /// <summary>The overlap of two rects, or empty if they don't overlap.</summary>
    public static Rect IntersectWith(this Rect a, Rect b)
    {
        a.Intersect(b);
        return a;
    }

    public static bool ContainsRect(this Rect outer, Rect inner) =>
        inner.Left >= outer.Left && inner.Top >= outer.Top &&
        inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;

    /// <summary>Snaps outward to whole pixels so exports never cut off a partial pixel.</summary>
    public static Rect RoundOut(this Rect r)
    {
        double left = Math.Floor(r.Left), top = Math.Floor(r.Top);
        return new Rect(left, top, Math.Ceiling(r.Right) - left, Math.Ceiling(r.Bottom) - top);
    }
}
