namespace Windshot.Video;

/// <summary>
/// Reads a video file's frames (as BGRA) or audio (as 16-bit PCM) in order, exactly, with
/// Media Foundation's source reader. Seeking lands on the keyframe before the time asked for;
/// callers skip forward to the exact frame.
/// </summary>
internal sealed unsafe class MediaReader : IDisposable
{
    private IntPtr _reader;
    private readonly uint _stream;

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    /// <summary>Video frames per second, or 0 if the file doesn't say.</summary>
    public double FrameRate { get; }

    /// <summary>How long the file is, or zero if it doesn't say.</summary>
    public TimeSpan Duration
    {
        get
        {
            // A PROPVARIANT holding VT_UI8 (100-ns units), from the media source as a whole.
            var variant = stackalloc byte[24];
            new Span<byte>(variant, 24).Clear();
            var key = MF.Duration;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, Guid*, byte*, int>)Com.Slot(_reader, 12))(_reader, 0xFFFFFFFF, &key, variant);
            return hr >= 0 && *(ushort*)variant == 21 ? TimeSpan.FromTicks((long)*(ulong*)(variant + 8)) : TimeSpan.Zero;
        }
    }

    static MediaReader() => Com.Check(Com.MFStartup(MF.Version, 0), "MFStartup");

    public MediaReader(string path, bool audio)
    {
        Com.Check(Com.MFCreateAttributes(out var attributes, 1), "MFCreateAttributes");
        // Lets the reader convert decoded video (NV12) to RGB32 for us.
        Com.SetUInt32(attributes, MF.EnableVideoProcessing, 1);
        Com.Check(Com.MFCreateSourceReaderFromURL(path, attributes, out _reader), "MFCreateSourceReaderFromURL");
        Com.Release(attributes);

        _stream = audio ? MF.FirstAudioStream : MF.FirstVideoStream;
        // Only the stream we want, so the reader doesn't decode (and queue up) the other.
        Call(4, 0xFFFFFFFE, 0, "SetStreamSelection(all, false)");
        Call(4, _stream, 1, "SetStreamSelection");

        Com.Check(Com.MFCreateMediaType(out var type), "MFCreateMediaType");
        Com.SetGuid(type, MF.MajorType, audio ? MF.Audio : MF.Video);
        Com.SetGuid(type, MF.Subtype, audio ? MF.Pcm : MF.Rgb32);
        if (audio)
            Com.SetUInt32(type, MF.BitsPerSample, 16);
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, uint*, IntPtr, int>)Com.Slot(_reader, 7))(_reader, _stream, null, type), "SetCurrentMediaType");
        Com.Release(type);

        IntPtr current;
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Com.Slot(_reader, 6))(_reader, _stream, &current), "GetCurrentMediaType");
        if (audio)
        {
            SampleRate = (int)Com.GetUInt32(current, MF.SamplesPerSecond);
            Channels = (int)Com.GetUInt32(current, MF.NumChannels);
        }
        else
        {
            ulong size = Com.GetUInt64(current, MF.FrameSize);
            Width = (int)(size >> 32);
            Height = (int)(size & 0xFFFFFFFF);
            Stride = Com.TryGetInt32(current, MF.DefaultStride, out int stride) ? stride : Width * 4;
            try
            {
                ulong rate = Com.GetUInt64(current, MF.FrameRate);
                uint numerator = (uint)(rate >> 32), denominator = (uint)rate;
                FrameRate = denominator > 0 ? numerator / (double)denominator : 0;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Not given: the caller assumes a usual rate.
            }
        }
        Com.Release(current);
    }

    private void Call(int slot, uint a, int b, string what) =>
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int, int>)Com.Slot(_reader, slot))(_reader, a, b), what);

    /// <summary>Jumps to the keyframe at or before <paramref name="time"/>.</summary>
    public void Seek(TimeSpan time)
    {
        // A PROPVARIANT holding VT_I8 (100-ns units).
        var variant = stackalloc byte[24];
        new Span<byte>(variant, 24).Clear();
        *(ushort*)variant = 20;
        *(long*)(variant + 8) = time.Ticks;
        var timeFormat = Guid.Empty;
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, byte*, int>)Com.Slot(_reader, 8))(_reader, &timeFormat, variant), "SetCurrentPosition");
    }

    /// <summary>The next sample's bytes and time, or null at the end. For video, rows are top-down BGRA, tightly packed.</summary>
    /// <summary>
    /// Hands out the same array for every frame, overwriting the last one, instead of a new one
    /// each time: for a caller done with each frame before reading the next (exporting), it saves
    /// allocating a whole frame (33 MB at 4K) per frame.
    /// </summary>
    public bool ReuseBuffer { get; set; }
    private byte[]? _reused;

    public (byte[] Data, TimeSpan Time)? Read()
    {
        while (true)
        {
            uint actual, flags;
            long timestamp;
            IntPtr sample;
            Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint*, uint*, long*, IntPtr*, int>)Com.Slot(_reader, 9))(
                _reader, _stream, 0, &actual, &flags, &timestamp, &sample), "ReadSample");
            if ((flags & MF.EndOfStream) != 0)
            {
                Com.Release(sample);
                return null;
            }
            if (sample == IntPtr.Zero)
                continue; // e.g. a format change notice
            try
            {
                // Video: the sample's own buffer, for its true row pitch (see TryCopy2D).
                if (_stream != MF.FirstAudioStream)
                {
                    IntPtr first;
                    if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Com.Slot(sample, 40))(sample, 0, &first) >= 0)
                    {
                        try
                        {
                            if (TryCopy2D(first) is { } pixels)
                                return (pixels, TimeSpan.FromTicks(timestamp));
                        }
                        finally
                        {
                            Com.Release(first);
                        }
                    }
                }
                IntPtr buffer;
                Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Com.Slot(sample, 41))(sample, &buffer), "ConvertToContiguousBuffer");
                try
                {
                    byte* data;
                    uint max, length;
                    Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)Com.Slot(buffer, 3))(buffer, &data, &max, &length), "Lock");
                    try
                    {
                        return (Copy(data, Stride, (int)length), TimeSpan.FromTicks(timestamp));
                    }
                    finally
                    {
                        ((delegate* unmanaged[Stdcall]<IntPtr, int>)Com.Slot(buffer, 4))(buffer);
                    }
                }
                finally
                {
                    Com.Release(buffer);
                }
            }
            finally
            {
                Com.Release(sample);
            }
        }
    }

    private static readonly Guid IID_IMF2DBuffer = new("7DC9D5F9-9ED9-44ec-9BBF-0600BB589FBB");

    /// <summary>
    /// A frame through IMF2DBuffer, which gives its true row pitch: decoders pad rows (a frame
    /// 1978 pixels wide may be stored 1984 wide), and the media type doesn't always say so.
    /// Null when the buffer isn't a 2D one.
    /// </summary>
    private byte[]? TryCopy2D(IntPtr buffer)
    {
        IntPtr buffer2D;
        var iid = IID_IMF2DBuffer;
        if (((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Com.Slot(buffer, 0))(buffer, &iid, &buffer2D) < 0)
            return null;
        try
        {
            byte* scanline0;
            int pitch;
            if (((delegate* unmanaged[Stdcall]<IntPtr, byte**, int*, int>)Com.Slot(buffer2D, 3))(buffer2D, &scanline0, &pitch) < 0)
                return null;
            try
            {
                // Scanline 0 is the top row, whichever way the rows are stored (a negative pitch
                // walks up through memory).
                return CopyRows(scanline0, pitch);
            }
            finally
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, int>)Com.Slot(buffer2D, 4))(buffer2D);
            }
        }
        finally
        {
            Com.Release(buffer2D);
        }
    }

    private byte[] Copy(byte* data, int stride, int length)
    {
        if (_stream == MF.FirstAudioStream)
            return new ReadOnlySpan<byte>(data, length).ToArray();
        // Without a pitch from the buffer, the media type's stride, unless the buffer's size
        // says otherwise: decoders pad the rows, and sometimes the height (to a multiple of 16),
        // while the media type still gives the unpadded stride.
        if (Height > 0 && length != Math.Abs((long)stride) * Height)
        {
            int padded = (Height + 15) & ~15;
            int rows = length % Height == 0 ? Height : length % padded == 0 ? padded : Height;
            if (length / rows >= Width * 4)
                stride = Math.Sign(stride == 0 ? 1 : stride) * (length / rows);
        }
        // A negative stride means the rows are stored bottom-up: start from the last one.
        return CopyRows(stride < 0 ? data + (Height - 1) * (long)-stride : data, stride);
    }

    /// <summary>Copies rows starting at the top one, <paramref name="pitch"/> bytes apart, into tightly packed, opaque BGRA.</summary>
    private byte[] CopyRows(byte* top, int pitch)
    {
        int row = Width * 4;
        var pixels = ReuseBuffer && _reused?.Length == row * Height ? _reused : new byte[row * Height];
        if (ReuseBuffer)
            _reused = pixels;
        for (int y = 0; y < Height; y++)
            new ReadOnlySpan<byte>(top + (long)y * pitch, row).CopyTo(pixels.AsSpan(y * row, row));
        // RGB32's fourth byte is padding, usually 0. Win2D would read that as transparent (so
        // scaling or blending a frame would let what's underneath show through): make it opaque.
        var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(pixels.AsSpan());
        var opaque = new System.Numerics.Vector<uint>(0xFF000000);
        int vectors = words.Length / System.Numerics.Vector<uint>.Count * System.Numerics.Vector<uint>.Count;
        var asVectors = System.Runtime.InteropServices.MemoryMarshal.Cast<uint, System.Numerics.Vector<uint>>(words[..vectors]);
        for (int i = 0; i < asVectors.Length; i++)
            asVectors[i] |= opaque;
        for (int i = vectors; i < words.Length; i++)
            words[i] |= 0xFF000000;
        return pixels;
    }

    public void Dispose()
    {
        Com.Release(_reader);
        _reader = IntPtr.Zero;
    }
}
