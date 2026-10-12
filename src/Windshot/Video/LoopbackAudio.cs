using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Windshot.Video;

/// <summary>
/// Records what the default speakers play (WASAPI loopback), as 16-bit PCM chunks stamped on
/// the same clock as captured video frames. Loopback delivers nothing while nothing plays, so
/// gaps are filled with silence, keeping the audio continuous and in step with the video.
/// </summary>
internal sealed unsafe class LoopbackAudio : IDisposable
{
    private static readonly Guid ClsidEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IidCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    private const uint StreamFlagsLoopback = 0x20000;
    private const uint BufferSilent = 0x2;

    private IntPtr _client, _capture;
    private Thread? _thread;
    private volatile bool _stopping;
    private long _startTicks; // 100-ns QPC time the recording starts
    private long _written;    // frames handed out so far

    public int SampleRate { get; }
    public int Channels { get; }
    private readonly int _sourceBits;
    private readonly bool _sourceFloat;

    /// <summary>16-bit interleaved PCM, with its time from the start of the recording.</summary>
    public BlockingCollection<AudioChunk> Chunks { get; } = new();

    /// <summary>Records silence instead of the sound, e.g. while the recording bar's sound switch is off.</summary>
    public volatile bool Muted;

    public long SilenceFrames { get; private set; }
    public long SoundFrames { get; private set; }

    public LoopbackAudio()
    {
        Com.Check(Com.CoCreateInstance(ClsidEnumerator, IntPtr.Zero, 23, IidEnumerator, out var enumerator), "Creating the device enumerator");
        IntPtr device;
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, int, int, IntPtr*, int>)Com.Slot(enumerator, 4))(enumerator, 0, 0, &device), "GetDefaultAudioEndpoint");
        Com.Release(enumerator);
        IntPtr client;
        var iid = IidAudioClient;
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, IntPtr, IntPtr*, int>)Com.Slot(device, 3))(device, &iid, 23, IntPtr.Zero, &client), "IMMDevice.Activate");
        Com.Release(device);
        _client = client;

        IntPtr format;
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Com.Slot(_client, 8))(_client, &format), "GetMixFormat");
        ushort tag = *(ushort*)format;
        Channels = *(ushort*)(format + 2);
        SampleRate = *(int*)(format + 4);
        _sourceBits = *(ushort*)(format + 14);
        // WAVE_FORMAT_IEEE_FLOAT, or WAVE_FORMAT_EXTENSIBLE whose subformat is float.
        _sourceFloat = tag == 3 || (tag == 0xFFFE && (*(Guid*)(format + 24)).ToString().StartsWith("00000003"));

        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, int, uint, long, long, IntPtr, IntPtr, int>)Com.Slot(_client, 3))(
            _client, 0, StreamFlagsLoopback, 10_000_000, 0, format, IntPtr.Zero), "IAudioClient.Initialize");
        Com.CoTaskMemFree(format);

        IntPtr capture;
        var iidCapture = IidCaptureClient;
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Com.Slot(_client, 14))(_client, &iidCapture, &capture), "GetService");
        _capture = capture;
    }

    public string Describe => $"{SampleRate} Hz, {Channels} ch, {_sourceBits}-bit {(_sourceFloat ? "float" : "int")}";

    public static long NowTicks => (long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency));

    public void Start(long startTicks)
    {
        _startTicks = startTicks;
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)Com.Slot(_client, 10))(_client), "IAudioClient.Start");
        _thread = new Thread(Run) { IsBackground = true, Name = "Loopback audio" };
        _thread.Start();
    }

    /// <summary>Stops at the given time, padding with silence up to it.</summary>
    public void Stop(long endTicks)
    {
        _stopping = true;
        _thread?.Join();
        ((delegate* unmanaged[Stdcall]<IntPtr, int>)Com.Slot(_client, 11))(_client);
        PadTo(FrameAt(endTicks));
        Chunks.CompleteAdding();
    }

    private long FrameAt(long ticks) => (long)((ticks - _startTicks) * (double)SampleRate / 10_000_000);

    private void Run()
    {
        try
        {
            Capture();
        }
        catch (Exception ex)
        {
            // E.g. the speakers were unplugged, or Bluetooth headphones went away
            // (AUDCLNT_E_DEVICE_INVALIDATED). An exception here would end the app, losing the
            // recording: carry on with silence instead, so the video and its timing are kept.
            Log.Write($"Recording sound stopped: {ex.Message}");
            while (!_stopping)
            {
                PadTo(FrameAt(NowTicks) - SampleRate / 20);
                Thread.Sleep(10);
            }
        }
    }

    private void Capture()
    {
        while (!_stopping)
        {
            bool any = false;
            while (true)
            {
                uint packet;
                Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Com.Slot(_capture, 5))(_capture, &packet), "GetNextPacketSize");
                if (packet == 0)
                    break;
                byte* data;
                uint frames, flags;
                ulong devicePosition, qpc;
                Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, ulong*, ulong*, int>)Com.Slot(_capture, 3))(
                    _capture, &data, &frames, &flags, &devicePosition, &qpc), "GetBuffer");
                Take(data, (int)frames, Muted || (flags & BufferSilent) != 0, (long)qpc);
                ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Com.Slot(_capture, 4))(_capture, frames);
                any = true;
            }
            // Nothing playing: keep the audio moving with silence, a little behind real time.
            if (!any)
                PadTo(FrameAt(NowTicks) - SampleRate / 20);
            Thread.Sleep(10);
        }
    }

    private void Take(byte* data, int frames, bool silent, long qpcTicks)
    {
        long at = FrameAt(qpcTicks);
        if (at < 0)
        {
            // Played before the recording started.
            int skip = (int)Math.Min(frames, -at);
            data += skip * Channels * (_sourceBits / 8);
            frames -= skip;
            at = 0;
        }
        if (at > _written)
            PadTo(at);
        int overlap = (int)Math.Min(frames, Math.Max(0, _written - at));
        frames -= overlap;
        if (frames <= 0)
            return;
        data += overlap * Channels * (_sourceBits / 8);

        var pcm = new byte[frames * Channels * 2];
        fixed (byte* outBytes = pcm)
        {
            short* output = (short*)outBytes;
            int count = frames * Channels;
            if (silent)
            {
                // already zero
            }
            else if (_sourceFloat && _sourceBits == 32)
            {
                float* input = (float*)data;
                for (int i = 0; i < count; i++)
                    output[i] = (short)Math.Clamp(input[i] * 32767f, -32768f, 32767f);
            }
            else if (_sourceBits == 16)
            {
                Buffer.MemoryCopy(data, output, pcm.Length, pcm.Length);
            }
            else
            {
                throw new NotSupportedException($"Audio format {Describe}");
            }
        }
        Emit(pcm, frames);
        SoundFrames += frames;
    }

    private void PadTo(long frame)
    {
        while (frame > _written)
        {
            int frames = (int)Math.Min(frame - _written, SampleRate / 10);
            Emit(new byte[frames * Channels * 2], frames);
            SilenceFrames += frames;
        }
    }

    private void Emit(byte[] pcm, int frames)
    {
        var time = TimeSpan.FromTicks(_written * 10_000_000 / SampleRate);
        _written += frames;
        Chunks.Add(new AudioChunk(pcm, time, TimeSpan.FromTicks(frames * 10_000_000L / SampleRate)));
    }

    public void Dispose()
    {
        Com.Release(_capture);
        Com.Release(_client);
        _capture = _client = IntPtr.Zero;
    }
}
