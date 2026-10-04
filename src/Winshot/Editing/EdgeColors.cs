using Windows.UI;

namespace Winshot.Editing;

[Flags]
internal enum Sides
{
    None = 0,
    Left = 1,
    Top = 2,
    Right = 4,
    Bottom = 8,
}

/// <summary>
/// Finds the dominant color along stretches of the screenshot's edges, so canvas added when
/// it grows can continue the background instead of being transparent.
/// </summary>
internal sealed class EdgeColors
{
    /// <summary>How much of a stretch one color must cover before we trust it as "the background".</summary>
    private const double MinShare = 0.4;

    /// <summary>Pixels in from each edge that are sampled.</summary>
    private const int Depth = 2;

    // Per side, the sampled colors (0xRRGGBB) along the edge: Depth samples per position.
    private readonly Dictionary<Sides, int[]> _edges = new();
    private readonly Dictionary<(Sides, int, int), Color?> _cache = new();

    public EdgeColors(byte[] bgra, int width, int height)
    {
        int depth = Math.Min(Depth, Math.Min(width, height));
        int Pixel(int x, int y)
        {
            int i = (y * width + x) * 4;
            return bgra[i + 2] << 16 | bgra[i + 1] << 8 | bgra[i];
        }

        var left = new int[height * depth];
        var right = new int[height * depth];
        for (int y = 0; y < height; y++)
        {
            for (int d = 0; d < depth; d++)
            {
                left[y * depth + d] = Pixel(d, y);
                right[y * depth + d] = Pixel(width - 1 - d, y);
            }
        }
        var top = new int[width * depth];
        var bottom = new int[width * depth];
        for (int x = 0; x < width; x++)
        {
            for (int d = 0; d < depth; d++)
            {
                top[x * depth + d] = Pixel(x, d);
                bottom[x * depth + d] = Pixel(x, height - 1 - d);
            }
        }

        _edges[Sides.Left] = left;
        _edges[Sides.Right] = right;
        _edges[Sides.Top] = top;
        _edges[Sides.Bottom] = bottom;
        _depth = depth;
    }

    private readonly int _depth;

    /// <summary>
    /// The dominant color along one edge between two positions (pixels along that edge), or
    /// null if no single color is common enough to look like a background.
    /// </summary>
    public Color? Dominant(Sides side, int from, int to)
    {
        var samples = _edges[side];
        int length = samples.Length / _depth;
        from = Math.Clamp(from, 0, length);
        to = Math.Clamp(to, from, length);
        if (to - from < 1)
            return null;
        if (_cache.TryGetValue((side, from, to), out var cached))
            return cached;

        // Quantize to 4 bits per channel to group near-identical shades, then average the
        // real colors inside the winning group.
        var counts = new Dictionary<int, (int Count, long R, long G, long B)>();
        for (int i = from * _depth; i < to * _depth; i++)
        {
            int c = samples[i];
            int key = (c >> 20 & 0xF) << 8 | (c >> 12 & 0xF) << 4 | (c >> 4 & 0xF);
            counts.TryGetValue(key, out var bucket);
            counts[key] = (bucket.Count + 1, bucket.R + (c >> 16 & 0xFF), bucket.G + (c >> 8 & 0xFF), bucket.B + (c & 0xFF));
        }

        Color? result = null;
        var top = counts.Values.MaxBy(b => b.Count);
        int total = (to - from) * _depth;
        if ((double)top.Count / total >= MinShare)
            result = Color.FromArgb(255, (byte)(top.R / top.Count), (byte)(top.G / top.Count), (byte)(top.B / top.Count));

        _cache[(side, from, to)] = result;
        return result;
    }
}
