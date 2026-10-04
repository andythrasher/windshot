namespace Windshot.Capture;

internal enum StitchResult
{
    /// <summary>Nothing moved.</summary>
    Unchanged,
    /// <summary>Scrolled down; new rows were added.</summary>
    Appended,
    /// <summary>Scrolled back up; nothing to add until it comes back down.</summary>
    ScrolledBack,
    /// <summary>Changed, but no overlap with the last frame was found (scrolled too fast, mid-animation, or the content changed).</summary>
    Lost,
    /// <summary>Reached the maximum height; the result is complete.</summary>
    Full,
}

/// <summary>
/// Builds one tall image from frames of the same screen area taken while it scrolls down.
/// Each frame is lined up against the last one that was used: the vertical offset where
/// the rows look the same is how far the content scrolled, and the rows that came into
/// view at the bottom get appended.
/// </summary>
/// <remarks>
/// Rows are compared by a coarse signature (the brightness of a few dozen strips across the
/// row), not exactly: browsers redraw scrolled content with slight differences (about one
/// pixel in 200 off by a shade or two), so exact matching almost never lines anything up.
/// Only columns that changed take part, so a fixed sidebar or the page margins beside the
/// scrolling content don't get in the way. Rows that stayed put (a sticky header) are left
/// out of the comparison; rows that stayed put at the bottom (a sticky footer) are kept out
/// of the middle and added once, at the very end.
/// </remarks>
internal sealed class ScrollStitcher
{
    /// <summary>Strips per row signature.</summary>
    private const int Strips = 32;
    /// <summary>A pixel counts as changed when its R+G+B moved by more than this; below it is redraw noise.</summary>
    private const int ChangedPixel = 24;
    /// <summary>Average R+G+B difference per pixel under which two rows count as the same.</summary>
    private const double SameRow = 1.5;
    /// <summary>Share of the compared rows that must line up for an offset to be accepted.</summary>
    private const double MinAgreement = 0.5;
    /// <summary>The runner-up offset may line up at most this share as many rows.</summary>
    private const double MaxRival = 0.7;
    /// <summary>Candidate offsets from the quick pass that get the full comparison.</summary>
    private const int Candidates = 8;
    /// <summary>A footer row may still have this share of its pixels change.</summary>
    private const double MostlyStayed = 0.05;

    private readonly int _width;
    private readonly int _height;
    private readonly int _maxHeight;
    private readonly int[] _first;
    private readonly List<int[]> _slices = new();
    /// <summary>Per column: positive when it mostly stayed put while the content scrolled.</summary>
    private readonly int[] _fixedVotes;
    private int[] _reference;
    /// <summary>Height of the sticky footer, once the first scroll has shown what stays put.</summary>
    private int? _footer;

    /// <param name="first">BGRA pixels, one int per pixel, row by row.</param>
    public ScrollStitcher(int[] first, int width, int height, int maxHeight)
    {
        _width = width;
        _height = height;
        _maxHeight = Math.Max(maxHeight, height);
        _first = first;
        _reference = first;
        _fixedVotes = new int[width];
    }

    public int Width => _width;

    /// <summary>Height of the image <see cref="Build"/> would produce right now.</summary>
    public int TotalHeight => _height + _slices.Sum(s => s.Length / _width);

    public StitchResult Add(int[] frame)
    {
        var a = _reference;
        var moving = MovingColumns(a, frame, out int changedRows);
        // A spinner or blinking caret changes a few rows forever; scrolling changes nearly all of them.
        if (changedRows <= Math.Max(2, _height / 33))
            return StitchResult.Unchanged;

        int columns = moving.Sum(r => r.End - r.Start);
        var sigA = new Signatures(a, _width, _height, moving);
        var sigB = new Signatures(frame, _width, _height, moving);

        // Rows that stayed put: a sticky header or footer, or a margin that never changes.
        var stayed = new bool[_height];
        for (int y = 0; y < _height; y++)
            stayed[y] = sigA.Distance(y, sigB, y) <= SameRow * columns;

        int footer = _footer ?? 0;
        int body = _height - footer;
        int minOverlap = Math.Max(16, body / 8);
        int maxOffset = body - minOverlap;
        if (maxOffset < 1)
            return StitchResult.Lost;

        // Each offset is scored by how many rows line up (look the same) there. Counting rather
        // than averaging means rows that legitimately don't line up, like content that slid
        // under a sticky footer, can't drown out the rest.
        long tolerance = (long)(SameRow * columns);

        // Quick pass on row brightness alone, to find a handful of plausible offsets.
        var quick = new List<(int Offset, int Matches)>();
        for (int d = -maxOffset; d <= maxOffset; d++)
        {
            if (d == 0)
                continue;
            int matches = 0, rows = 0;
            for (int y = Math.Max(0, -d); y < Math.Min(body, body - d); y++)
            {
                if (stayed[y] || !(sigB.Detail[y] || sigA.Detail[y + d]))
                    continue;
                rows++;
                if (Math.Abs(sigB.Total[y] - sigA.Total[y + d]) <= tolerance)
                    matches++;
            }
            if (rows >= 8 && matches > 0)
                quick.Add((d, matches));
        }
        if (quick.Count == 0)
            return StitchResult.Lost;

        // The full comparison for the best few.
        var scored = quick.OrderByDescending(q => q.Matches).Take(Candidates)
            .Select(q => FullScore(sigA, sigB, stayed, body, q.Offset, tolerance))
            .OrderByDescending(s => s.Matches).ToList();
        var (offset, best, compared) = scored[0];
        if (best < MinAgreement * compared || best < 8)
            return StitchResult.Lost;
        // Look-alike content (lines of text at a regular spacing, blank space) can line up at
        // more than one offset; take the match only when it clearly beats the next one.
        int rival = scored.Skip(1).Where(s => Math.Abs(s.Offset - offset) > 2).Select(s => s.Matches).DefaultIfEmpty(0).Max();
        if (rival > best * MaxRival)
            return StitchResult.Lost;
        if (offset < 0)
            return StitchResult.ScrolledBack;

        _footer ??= FindFooter(a, frame, moving);
        footer = _footer.Value;
        body = _height - footer;
        if (offset >= body)
            return StitchResult.Lost;
        VoteColumns(a, frame, offset, body, stayed);

        // The rows that scrolled into view at the bottom of the scrolling part.
        int added = Math.Min(offset, _maxHeight - TotalHeight);
        if (added <= 0)
            return StitchResult.Full;
        var slice = new int[added * _width];
        Array.Copy(frame, (body - offset) * _width, slice, 0, slice.Length);
        _slices.Add(slice);
        _reference = frame;
        return added < offset || TotalHeight >= _maxHeight ? StitchResult.Full : StitchResult.Appended;
    }

    /// <summary>How many of the compared rows look the same with the last frame shifted by <paramref name="offset"/>.</summary>
    private static (int Offset, int Matches, int Rows) FullScore(Signatures a, Signatures b, bool[] stayed, int body, int offset, long tolerance)
    {
        int matches = 0, rows = 0;
        for (int y = Math.Max(0, -offset); y < Math.Min(body, body - offset); y++)
        {
            if (stayed[y] || !(b.Detail[y] || a.Detail[y + offset]))
                continue;
            rows++;
            if (b.Distance(y, a, y + offset) <= tolerance)
                matches++;
        }
        return (offset, matches, rows);
    }

    /// <summary>Rows at the bottom that (nearly all) stayed put while the content moved, up to a third of the height.</summary>
    /// <remarks>
    /// Erring large is harmless: rows below the cut come from the last frame, so the result
    /// still runs on continuously, and a blank stretch of content that merely looked static
    /// just arrives there instead. Erring small repeats the footer after every slice, so
    /// there's no attempt to tell a flat-colored footer from blank content that scrolled, and
    /// a row still counts when a little of it changed (a link preview, an animation).
    /// </remarks>
    private int FindFooter(int[] a, int[] b, List<(int Start, int End)> moving)
    {
        int columns = moving.Sum(r => r.End - r.Start);
        int footer = 0;
        for (int y = _height - 1; y >= _height * 2 / 3; y--, footer++)
        {
            int changed = 0;
            foreach (var (start, end) in moving)
            {
                for (int x = start; x < end; x++)
                {
                    int i = y * _width + x;
                    if (Math.Abs(Brightness(a[i]) - Brightness(b[i])) > ChangedPixel)
                        changed++;
                }
            }
            if (changed > columns * MostlyStayed)
                break;
        }
        return footer;
    }

    /// <summary>
    /// For each column, whether it moved with the content (it matches the last frame shifted)
    /// or not (a scrollbar, a window border, a window overlapping the area), tallied over
    /// every scroll. Columns that don't move with the content aren't repeated in each slice.
    /// </summary>
    /// <param name="stayed">Rows that stayed put (a header); they'd make every column look fixed.</param>
    private void VoteColumns(int[] a, int[] b, int offset, int body, bool[] stayed)
    {
        for (int x = 0; x < _width; x++)
        {
            int shifted = 0, inPlace = 0;
            for (int y = 0; y < body - offset; y++)
            {
                if (stayed[y])
                    continue;
                int pb = Brightness(b[y * _width + x]);
                if (Math.Abs(pb - Brightness(a[(y + offset) * _width + x])) > ChangedPixel)
                    shifted++;
                if (Math.Abs(pb - Brightness(a[y * _width + x])) > ChangedPixel)
                    inPlace++;
            }
            // Ties (flat columns, which look the same either way) count as moving with the content.
            if (inPlace < shifted)
                _fixedVotes[x]++;
            else if (shifted < inPlace)
                _fixedVotes[x]--;
        }
    }

    /// <summary>Height of the footer found at the bottom, for diagnostics.</summary>
    public int Footer => _footer ?? 0;

    /// <summary>
    /// Everything captured so far: first frame, the new rows in order, then the footer.
    /// Columns that didn't move with the content (see <see cref="VoteColumns"/>) are laid out
    /// differently: their top from the first frame, their bottom from the last frame, and in
    /// between their most common color, so a scrollbar or window edge reads as one long one.
    /// </summary>
    public CapturedImage Build(double scale, System.Drawing.Rectangle desktopBounds)
    {
        int footer = _footer ?? 0;
        int body = _height - footer;
        int height = TotalHeight;
        var pixels = new int[_width * height];
        int at = 0;
        void Copy(int[] source, int startRow, int rows)
        {
            Array.Copy(source, startRow * _width, pixels, at, rows * _width);
            at += rows * _width;
        }

        Copy(_first, 0, body);
        foreach (var slice in _slices)
            Copy(slice, 0, slice.Length / _width);
        Copy(_reference, body, footer);

        int added = height - _height;
        if (added > 0)
        {
            // Rows of fixed columns that stay with the bottom (a scrollbar's down arrow, say).
            int bottomPart = body / 4;
            for (int x = 0; x < _width; x++)
            {
                if (_fixedVotes[x] <= 0)
                    continue;
                int fill = MostCommon(_first, x, 0, body);
                for (int y = body - bottomPart; y < body - bottomPart + added; y++)
                    pixels[y * _width + x] = fill;
                for (int y = 0; y < bottomPart; y++)
                    pixels[(body - bottomPart + added + y) * _width + x] = _reference[(body - bottomPart + y) * _width + x];
            }
        }

        var bytes = new byte[pixels.Length * 4];
        Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
        return new CapturedImage(bytes, _width, height, scale, desktopBounds, IsScrolling: true);
    }

    private int MostCommon(int[] pixels, int x, int fromRow, int toRow)
    {
        var counts = new Dictionary<int, int>();
        for (int y = fromRow; y < toRow; y++)
        {
            int p = pixels[y * _width + x];
            counts[p] = counts.GetValueOrDefault(p) + 1;
        }
        return counts.Count == 0 ? 0 : counts.MaxBy(c => c.Value).Key;
    }

    private static int Brightness(int p) => (p & 255) + ((p >> 8) & 255) + ((p >> 16) & 255);

    /// <summary>Column ranges [start, end) where something clearly changed between the two frames (not just redraw noise).</summary>
    /// <param name="changedRows">How many rows clearly changed.</param>
    private List<(int Start, int End)> MovingColumns(int[] a, int[] b, out int changedRows)
    {
        var moving = new bool[_width];
        changedRows = 0;
        for (int y = 0; y < _height; y++)
        {
            var rowA = a.AsSpan(y * _width, _width);
            var rowB = b.AsSpan(y * _width, _width);
            if (rowA.SequenceEqual(rowB))
                continue;
            bool changed = false;
            for (int x = 0; x < _width; x++)
            {
                if (rowA[x] != rowB[x] && Math.Abs(Brightness(rowA[x]) - Brightness(rowB[x])) > ChangedPixel)
                {
                    moving[x] = true;
                    changed = true;
                }
            }
            if (changed)
                changedRows++;
        }

        var ranges = new List<(int, int)>();
        for (int x = 0; x < _width; x++)
        {
            if (!moving[x])
                continue;
            int start = x;
            while (x < _width && moving[x])
                x++;
            ranges.Add((start, x));
        }
        return ranges;
    }

    /// <summary>Per row, over the given columns: the summed R+G+B of each of a few strips, their total, and whether the row has any detail.</summary>
    private sealed class Signatures
    {
        public readonly int[] Strip;
        public readonly long[] Total;
        public readonly bool[] Detail;

        public Signatures(int[] pixels, int width, int height, List<(int Start, int End)> columns)
        {
            Strip = new int[height * Strips];
            Total = new long[height];
            Detail = new bool[height];
            int count = columns.Sum(c => c.End - c.Start);
            for (int y = 0; y < height; y++)
            {
                int row = y * width, i = 0, min = int.MaxValue, max = 0;
                long total = 0;
                foreach (var (start, end) in columns)
                {
                    for (int x = start; x < end; x++, i++)
                    {
                        int v = Brightness(pixels[row + x]);
                        Strip[y * Strips + (int)((long)i * Strips / count)] += v;
                        total += v;
                        min = Math.Min(min, v);
                        max = Math.Max(max, v);
                    }
                }
                Total[y] = total;
                Detail[y] = max - min > ChangedPixel;
            }
        }

        /// <summary>Sum of strip differences between row <paramref name="y"/> here and row <paramref name="otherY"/> there.</summary>
        public long Distance(int y, Signatures other, int otherY)
        {
            long sum = 0;
            for (int k = 0; k < Strips; k++)
                sum += Math.Abs(Strip[y * Strips + k] - other.Strip[otherY * Strips + k]);
            return sum;
        }
    }
}
