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
                IntPtr buffer;
                Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Com.Slot(sample, 41))(sample, &buffer), "ConvertToContiguousBuffer");
                try
                {
                    byte* data;
                    uint max, length;
                    Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)Com.Slot(buffer, 3))(buffer, &data, &max, &length), "Lock");
                    try
                    {
                        return (Copy(data, (int)length), TimeSpan.FromTicks(timestamp));
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

    private byte[] Copy(byte* data, int length)
    {
        if (_stream == MF.FirstAudioStream)
            return new ReadOnlySpan<byte>(data, length).ToArray();
        int row = Width * 4;
        var pixels = new byte[row * Height];
        int stride = Math.Abs(Stride);
        for (int y = 0; y < Height; y++)
        {
            // A negative stride means the rows are stored bottom-up.
            int from = (Stride < 0 ? Height - 1 - y : y) * stride;
            new ReadOnlySpan<byte>(data + from, row).CopyTo(pixels.AsSpan(y * row, row));
        }
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
