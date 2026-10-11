namespace Windshot.Video;

/// <summary>
/// Writes an animated GIF, the way screen-recording GIFs stay small: each frame stores only
/// the rectangle that changed, with unchanged pixels inside it transparent, so the previous
/// frame shows through; each has its own palette of up to 255 colors (median cut over the
/// pixels it stores), and loops forever. No dithering: screen content is flat colors and text,
/// which dithering only speckles.
/// </summary>
internal sealed class GifWriter : IDisposable
{
    private const byte Transparent = 255;
    /// <summary>
    /// Color differences this small (sum over R, G, B) count as unchanged, absorbing the faint
    /// noise video compression adds. Kept tiny: what it skips stays on screen, so a larger value
    /// leaves visible ghosts where something moved away.
    /// </summary>
    private const int Tolerance = 3;

    private readonly FileStream _file;
    private readonly int _width, _height;
    /// <summary>The source colors last sent for each pixel (0xRRGGBB), or -1 before the first frame.</summary>
    private readonly int[] _shown;
    private bool _finished;

    public GifWriter(string path, int width, int height)
    {
        _width = width;
        _height = height;
        _shown = new int[width * height];
        Array.Fill(_shown, -1);
        _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        _file.Write("GIF89a"u8);
        WriteShort(width);
        WriteShort(height);
        _file.WriteByte(0x70); // no global color table; 8 bits of color resolution
        _file.WriteByte(0);    // background color index
        _file.WriteByte(0);    // square pixels
        // Loop forever (the NETSCAPE2.0 application extension).
        _file.Write([0x21, 0xFF, 0x0B]);
        _file.Write("NETSCAPE2.0"u8);
        _file.Write([0x03, 0x01, 0x00, 0x00, 0x00]);
    }

    /// <param name="bgra">The frame, top-down BGRA (alpha ignored).</param>
    /// <param name="delay">How long it shows, in hundredths of a second.</param>
    public void AddFrame(byte[] bgra, int delay)
    {
        // The rectangle around everything that changed.
        int left = _width, top = _height, right = -1, bottom = -1;
        var source = new int[_width * _height];
        for (int y = 0; y < _height; y++)
        {
            int row = y * _width;
            for (int x = 0; x < _width; x++)
            {
                int i = (row + x) * 4;
                int color = bgra[i + 2] << 16 | bgra[i + 1] << 8 | bgra[i];
                source[row + x] = color;
                if (!Same(_shown[row + x], color))
                {
                    if (x < left) left = x;
                    if (x > right) right = x;
                    if (y < top) top = y;
                    if (y > bottom) bottom = y;
                }
            }
        }
        if (right < 0)
        {
            // Nothing changed: a single transparent pixel keeps the timing.
            left = top = right = bottom = 0;
        }
        int w = right - left + 1, h = bottom - top + 1;

        // What this frame stores, and a palette for it.
        var changed = new bool[w * h];
        var histogram = new Histogram();
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int at = (top + y) * _width + left + x;
                if (!Same(_shown[at], source[at]))
                {
                    changed[y * w + x] = true;
                    histogram.Add(source[at]);
                }
            }
        }
        var palette = histogram.Palette(255);
        var indices = new byte[w * h];
        var lookup = new short[32768];
        Array.Fill(lookup, (short)-1);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int at = (top + y) * _width + left + x;
                if (!changed[y * w + x])
                {
                    indices[y * w + x] = Transparent;
                    continue;
                }
                int color = source[at];
                indices[y * w + x] = palette.Exact.TryGetValue(color, out byte exact) ? exact : Nearest(palette.Colors, lookup, color);
                _shown[at] = color;
            }
        }

        // Graphic control extension: delay, keep this frame under the next, transparency.
        _file.Write([0x21, 0xF9, 0x04, (1 << 2) | 1]);
        WriteShort(Math.Clamp(delay, 0, 65535));
        _file.WriteByte(Transparent);
        _file.WriteByte(0);
        // Image descriptor, with a 256-entry local color table.
        _file.WriteByte(0x2C);
        WriteShort(left);
        WriteShort(top);
        WriteShort(w);
        WriteShort(h);
        _file.WriteByte(0x87);
        var table = new byte[256 * 3];
        for (int i = 0; i < palette.Colors.Count; i++)
        {
            int c = palette.Colors[i];
            table[i * 3] = (byte)(c >> 16);
            table[i * 3 + 1] = (byte)(c >> 8);
            table[i * 3 + 2] = (byte)c;
        }
        _file.Write(table);
        Lzw.Write(_file, indices);
    }

    private static bool Same(int a, int b)
    {
        if (a < 0)
            return false;
        int dr = Math.Abs((a >> 16 & 0xFF) - (b >> 16 & 0xFF));
        int dg = Math.Abs((a >> 8 & 0xFF) - (b >> 8 & 0xFF));
        int db = Math.Abs((a & 0xFF) - (b & 0xFF));
        return dr + dg + db <= Tolerance;
    }

    /// <summary>The closest palette color, cached per 15-bit color.</summary>
    private static byte Nearest(List<int> palette, short[] lookup, int color)
    {
        int key = (color >> 9 & 0x7C00) | (color >> 6 & 0x3E0) | (color >> 3 & 0x1F);
        if (lookup[key] >= 0)
            return (byte)lookup[key];
        int r = color >> 16 & 0xFF, g = color >> 8 & 0xFF, b = color & 0xFF;
        int best = 0, bestDistance = int.MaxValue;
        for (int i = 0; i < palette.Count; i++)
        {
            int p = palette[i];
            int dr = r - (p >> 16 & 0xFF), dg = g - (p >> 8 & 0xFF), db = b - (p & 0xFF);
            // Weighted toward green, which the eye is most sensitive to.
            int distance = 2 * dr * dr + 4 * dg * dg + 3 * db * db;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }
        lookup[key] = (short)best;
        return (byte)best;
    }

    public void Finish()
    {
        if (_finished)
            return;
        _finished = true;
        _file.WriteByte(0x3B);
        _file.Flush();
    }

    public void Dispose() => _file.Dispose();

    private void WriteShort(int value)
    {
        _file.WriteByte((byte)value);
        _file.WriteByte((byte)(value >> 8));
    }

    /// <summary>The colors a frame stores, and a palette for them.</summary>
    private sealed class Histogram
    {
        private readonly Dictionary<int, int> _exact = new();
        private readonly int[] _count = new int[32768];
        private readonly long[] _r = new long[32768], _g = new long[32768], _b = new long[32768];
        private bool _tooMany;

        public void Add(int color)
        {
            if (!_tooMany)
            {
                _exact[color] = _exact.GetValueOrDefault(color) + 1;
                if (_exact.Count > 255)
                    _tooMany = true;
            }
            int key = (color >> 9 & 0x7C00) | (color >> 6 & 0x3E0) | (color >> 3 & 0x1F);
            _count[key]++;
            _r[key] += color >> 16 & 0xFF;
            _g[key] += color >> 8 & 0xFF;
            _b[key] += color & 0xFF;
        }

        /// <summary>Exact colors when there are few enough (common for screens), else a median cut.</summary>
        public (List<int> Colors, Dictionary<int, byte> Exact) Palette(int size)
        {
            if (!_tooMany)
            {
                var colors = _exact.Keys.ToList();
                var exact = new Dictionary<int, byte>();
                for (int i = 0; i < colors.Count; i++)
                    exact[colors[i]] = (byte)i;
                if (colors.Count == 0)
                    colors.Add(0);
                return (colors, exact);
            }
            return (MedianCut(size), new Dictionary<int, byte>());
        }

        private List<int> MedianCut(int size)
        {
            var bins = new List<int>();
            for (int key = 0; key < _count.Length; key++)
            {
                if (_count[key] > 0)
                    bins.Add(key);
            }
            var boxes = new List<List<int>> { bins };
            while (boxes.Count < size)
            {
                // Split the box with the widest spread (weighted by how many pixels it holds).
                int pick = -1;
                long pickScore = 0;
                for (int i = 0; i < boxes.Count; i++)
                {
                    if (boxes[i].Count < 2)
                        continue;
                    var (spread, _) = Spread(boxes[i]);
                    long score = (long)spread * Weight(boxes[i]);
                    if (score > pickScore)
                    {
                        pickScore = score;
                        pick = i;
                    }
                }
                if (pick < 0)
                    break;
                var box = boxes[pick];
                var (_, channel) = Spread(box);
                box.Sort((a, b) => Channel(a, channel).CompareTo(Channel(b, channel)));
                long half = Weight(box) / 2, running = 0;
                int split = 1;
                for (int i = 0; i < box.Count - 1; i++)
                {
                    running += _count[box[i]];
                    if (running >= half)
                    {
                        split = i + 1;
                        break;
                    }
                }
                boxes[pick] = box.GetRange(0, split);
                boxes.Add(box.GetRange(split, box.Count - split));
            }

            var palette = new List<int>(boxes.Count);
            foreach (var box in boxes)
            {
                long n = 0, r = 0, g = 0, b = 0;
                foreach (int key in box)
                {
                    n += _count[key];
                    r += _r[key];
                    g += _g[key];
                    b += _b[key];
                }
                palette.Add(n == 0 ? 0 : (int)(r / n) << 16 | (int)(g / n) << 8 | (int)(b / n));
            }
            return palette;
        }

        private long Weight(List<int> box)
        {
            long n = 0;
            foreach (int key in box)
                n += _count[key];
            return n;
        }

        private static int Channel(int key, int channel) => channel switch
        {
            0 => key >> 10 & 0x1F,
            1 => key >> 5 & 0x1F,
            _ => key & 0x1F,
        };

        private static (int Spread, int Channel) Spread(List<int> box)
        {
            int best = 0, channel = 0;
            for (int c = 0; c < 3; c++)
            {
                int lo = 31, hi = 0;
                foreach (int key in box)
                {
                    int v = Channel(key, c);
                    if (v < lo) lo = v;
                    if (v > hi) hi = v;
                }
                if (hi - lo > best)
                {
                    best = hi - lo;
                    channel = c;
                }
            }
            return (best, channel);
        }
    }

    /// <summary>GIF's LZW compression, 8-bit codes growing to 12 bits, with the classic hashed string table.</summary>
    private static class Lzw
    {
        private const int Bits = 12;
        private const int TableSize = 5003;
        private const int MaxMaxCode = 1 << Bits;

        public static void Write(Stream output, byte[] pixels)
        {
            const int initialCodeSize = 8;
            output.WriteByte(initialCodeSize);
            var encoder = new Encoder(output, initialCodeSize + 1);
            encoder.Compress(pixels);
            output.WriteByte(0); // block terminator
        }

        private sealed class Encoder(Stream output, int initBits)
        {
            private readonly int[] _hashes = new int[TableSize];
            private readonly int[] _codes = new int[TableSize];
            private readonly byte[] _block = new byte[256];
            private int _blockLength;
            private int _bits = initBits, _maxCode = (1 << initBits) - 1;
            private readonly int _clearCode = 1 << (initBits - 1);
            private int _nextCode;
            private bool _clearing;
            private int _accumulator, _accumulated;

            public void Compress(byte[] pixels)
            {
                int eof = _clearCode + 1;
                _nextCode = _clearCode + 2;
                int shift = 0;
                for (int f = TableSize; f < 65536; f *= 2)
                    shift++;
                shift = 8 - shift;
                Array.Fill(_hashes, -1);
                Output(_clearCode);

                int entry = pixels.Length > 0 ? pixels[0] : 0;
                for (int p = 1; p < pixels.Length; p++)
                {
                    int c = pixels[p];
                    int fcode = (c << Bits) + entry;
                    int i = (c << shift) ^ entry;
                    if (_hashes[i] == fcode)
                    {
                        entry = _codes[i];
                        continue;
                    }
                    if (_hashes[i] >= 0)
                    {
                        int step = i == 0 ? 1 : TableSize - i;
                        bool found = false;
                        do
                        {
                            i -= step;
                            if (i < 0)
                                i += TableSize;
                            if (_hashes[i] == fcode)
                            {
                                entry = _codes[i];
                                found = true;
                                break;
                            }
                        }
                        while (_hashes[i] >= 0);
                        if (found)
                            continue;
                    }
                    Output(entry);
                    entry = c;
                    if (_nextCode < MaxMaxCode)
                    {
                        _codes[i] = _nextCode++;
                        _hashes[i] = fcode;
                    }
                    else
                    {
                        // Table full: start over.
                        Array.Fill(_hashes, -1);
                        _nextCode = _clearCode + 2;
                        _clearing = true;
                        Output(_clearCode);
                    }
                }
                Output(entry);
                Output(eof);
            }

            private void Output(int code)
            {
                _accumulator &= (1 << _accumulated) - 1;
                _accumulator = _accumulated > 0 ? _accumulator | code << _accumulated : code;
                _accumulated += _bits;
                while (_accumulated >= 8)
                {
                    Emit((byte)_accumulator);
                    _accumulator >>= 8;
                    _accumulated -= 8;
                }
                // Codes grow a bit when the table outgrows them; a clear starts them small again.
                if (_nextCode > _maxCode || _clearing)
                {
                    if (_clearing)
                    {
                        _bits = initBits;
                        _maxCode = (1 << _bits) - 1;
                        _clearing = false;
                    }
                    else
                    {
                        _bits++;
                        _maxCode = _bits == Bits ? MaxMaxCode : (1 << _bits) - 1;
                    }
                }
                if (code == _clearCode + 1)
                {
                    while (_accumulated > 0)
                    {
                        Emit((byte)_accumulator);
                        _accumulator >>= 8;
                        _accumulated -= 8;
                    }
                    Flush();
                }
            }

            private void Emit(byte b)
            {
                _block[_blockLength++] = b;
                if (_blockLength >= 254)
                    Flush();
            }

            private void Flush()
            {
                if (_blockLength == 0)
                    return;
                output.WriteByte((byte)_blockLength);
                output.Write(_block, 0, _blockLength);
                _blockLength = 0;
            }
        }
    }
}
