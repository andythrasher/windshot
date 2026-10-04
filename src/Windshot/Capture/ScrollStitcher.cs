namespace Windshot.Capture;

internal enum StitchResult
{
    /// <summary>Nothing moved.</summary>
    Unchanged,
    /// <summary>Scrolled down; new rows were added.</summary>
    Appended,
    /// <summary>Scrolled back up; nothing to add until it comes back down.</summary>
    ScrolledBack,
    /// <summary>Changed, but no overlap with the last frame was found (scrolled too fast, or the content changed).</summary>
    Lost,
    /// <summary>Reached the maximum height; the result is complete.</summary>
    Full,
}

/// <summary>
/// Builds one tall image from frames of the same screen area taken while it scrolls down.
/// Each frame is matched against the last one that was used by comparing rows: the offset
/// that lines up the most rows is how far the content scrolled, and the rows that came into
/// view at the bottom get appended.
/// </summary>
/// <remarks>
/// Only columns that changed take part in matching, so a fixed sidebar or the page margins
/// beside the scrolling content don't get in the way. Rows that stayed put at the top (a
/// sticky header) can't line up with anything and are ignored; rows that stayed put at the
/// bottom (a sticky footer) are kept out of the middle and added once, at the very end.
/// </remarks>
internal sealed class ScrollStitcher
{
    /// <summary>Rows that show up more often than this (blank lines, repeated borders) can't place anything.</summary>
    private const int MaxRepeats = 4;
    private const int MinVotes = 3;
    private const double MinAgreement = 0.5;

    private readonly int _width;
    private readonly int _height;
    private readonly int _maxHeight;
    private readonly int[] _first;
    private readonly List<int[]> _slices = new();
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
    }

    public int Width => _width;

    /// <summary>Height of the image <see cref="Build"/> would produce right now.</summary>
    public int TotalHeight => _height + _slices.Sum(s => s.Length / _width);

    public StitchResult Add(int[] frame)
    {
        var a = _reference;
        var moving = MovingColumns(a, frame);
        if (moving.Count == 0)
            return StitchResult.Unchanged;

        var (hashA, infoA) = HashRows(a, moving);
        var (hashB, infoB) = HashRows(frame, moving);
        int footer = _footer ?? 0;
        int body = _height - footer;

        // Where each row of the last frame is, by content.
        var rowsByHash = new Dictionary<ulong, List<int>>();
        for (int y = 0; y < body; y++)
        {
            if (!infoA[y])
                continue;
            if (!rowsByHash.TryGetValue(hashA[y], out var list))
                rowsByHash[hashA[y]] = list = new List<int>();
            list.Add(y);
        }

        // Each changed row of the new frame votes for how far the content moved to get there.
        // Index _height + d holds the votes for an offset of d (negative: scrolled back up).
        var votes = new int[_height * 2];
        for (int y = 0; y < body; y++)
        {
            if (!infoB[y] || hashA[y] == hashB[y] || !rowsByHash.TryGetValue(hashB[y], out var matches) || matches.Count > MaxRepeats)
                continue;
            foreach (int ya in matches)
            {
                if (ya != y)
                    votes[_height + ya - y]++;
            }
        }

        int best = 0;
        for (int i = 1; i < votes.Length; i++)
        {
            if (votes[i] > votes[best])
                best = i;
        }
        int offset = best - _height;
        if (votes[best] < MinVotes)
            return StitchResult.Lost;

        // Of the rows that changed and should still be on screen at that offset, enough must agree;
        // otherwise a few look-alike rows (borders, repeated lines) could fake a match.
        int candidates = 0;
        for (int y = Math.Max(0, -offset); y < Math.Min(body, body - offset); y++)
        {
            if (infoB[y] && hashA[y] != hashB[y])
                candidates++;
        }
        if (votes[best] < MinAgreement * candidates)
            return StitchResult.Lost;
        if (offset < 0)
            return StitchResult.ScrolledBack;

        _footer ??= FindFooter(hashA, hashB);
        footer = _footer.Value;
        body = _height - footer;
        if (offset >= body)
            return StitchResult.Lost;

        // The rows that scrolled into view at the bottom of the scrolling part.
        int rows = Math.Min(offset, _maxHeight - TotalHeight);
        if (rows <= 0)
            return StitchResult.Full;
        var slice = new int[rows * _width];
        Array.Copy(frame, (body - offset) * _width, slice, 0, slice.Length);
        _slices.Add(slice);
        _reference = frame;
        return rows < offset || TotalHeight >= _maxHeight ? StitchResult.Full : StitchResult.Appended;
    }

    /// <summary>Rows at the bottom that stayed put while the content moved, up to a third of the height.</summary>
    /// <remarks>
    /// Erring large is harmless: rows below the cut come from the last frame, so the result
    /// still runs on continuously, and a blank stretch of content that merely looked static
    /// just arrives there instead. Erring small repeats the footer after every slice, so
    /// there's no attempt to tell a flat-colored footer from blank content that scrolled.
    /// </remarks>
    private int FindFooter(ulong[] hashA, ulong[] hashB)
    {
        int footer = 0;
        for (int y = _height - 1; y >= _height * 2 / 3 && hashA[y] == hashB[y]; y--)
            footer++;
        return footer;
    }

    /// <summary>Everything captured so far: first frame, the new rows in order, then the footer.</summary>
    public CapturedImage Build(double scale, System.Drawing.Rectangle desktopBounds)
    {
        int footer = _footer ?? 0;
        int height = TotalHeight;
        var pixels = new byte[_width * height * 4];
        int at = 0;
        void Copy(int[] source, int startRow, int rows)
        {
            Buffer.BlockCopy(source, startRow * _width * 4, pixels, at, rows * _width * 4);
            at += rows * _width * 4;
        }

        Copy(_first, 0, _height - footer);
        foreach (var slice in _slices)
            Copy(slice, 0, slice.Length / _width);
        Copy(_reference, _height - footer, footer);
        return new CapturedImage(pixels, _width, height, scale, desktopBounds, IsScrolling: true);
    }

    /// <summary>Column ranges [start, end) where anything differs between the two frames.</summary>
    private List<(int Start, int End)> MovingColumns(int[] a, int[] b)
    {
        var moving = new bool[_width];
        for (int y = 0; y < _height; y++)
        {
            var rowA = a.AsSpan(y * _width, _width);
            var rowB = b.AsSpan(y * _width, _width);
            if (rowA.SequenceEqual(rowB))
                continue;
            for (int x = 0; x < _width; x++)
            {
                if (rowA[x] != rowB[x])
                    moving[x] = true;
            }
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

    /// <summary>A hash of each row over the given columns, and whether the row has any detail there (isn't one flat color).</summary>
    private (ulong[] Hashes, bool[] Informative) HashRows(int[] pixels, List<(int Start, int End)> columns)
    {
        var hashes = new ulong[_height];
        var informative = new bool[_height];
        for (int y = 0; y < _height; y++)
        {
            int row = y * _width;
            int first = pixels[row + columns[0].Start];
            ulong hash = 14695981039346656037;
            bool detail = false;
            foreach (var (start, end) in columns)
            {
                for (int x = start; x < end; x++)
                {
                    int p = pixels[row + x];
                    hash = (hash ^ (uint)p) * 1099511628211;
                    detail |= p != first;
                }
            }
            hashes[y] = hash;
            informative[y] = detail;
        }
        return (hashes, informative);
    }
}
