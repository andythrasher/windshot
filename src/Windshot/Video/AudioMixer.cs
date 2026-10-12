using System.Collections.Concurrent;

namespace Windshot.Video;

/// <summary>
/// Mixes the PC's sound with the microphone into one track, in the first source's format.
/// Every source is a continuous timeline from the start of the recording (silence fills its
/// gaps), so they line up sample for sample; a source in another format is converted on the way
/// in (channels mapped, rate changed by linear interpolation, which is plenty for speech). A
/// source added partway through (the microphone switched on mid-recording) joins from then on.
/// </summary>
internal sealed class AudioMixer
{
    private readonly object _lock = new();
    private readonly List<Source> _sources = new();
    private Thread? _thread;
    private long _written; // output frames handed out so far

    public int SampleRate { get; }
    public int Channels { get; }

    /// <summary>The mix, 16-bit interleaved PCM, timed from the start of the recording.</summary>
    public BlockingCollection<AudioChunk> Chunks { get; } = new();

    public AudioMixer(LoopbackAudio main) : this(main.Chunks, main.SampleRate, main.Channels)
    {
    }

    /// <param name="chunks">The first source: its format is the mix's.</param>
    public AudioMixer(BlockingCollection<AudioChunk> chunks, int sampleRate, int channels)
    {
        SampleRate = sampleRate;
        Channels = channels;
        _sources.Add(new Source(chunks, sampleRate, channels, this, skip: 0));
    }

    /// <summary>Mixes in another source from now on.</summary>
    public void Add(LoopbackAudio source) => Add(source.Chunks, source.SampleRate, source.Channels);

    public void Add(BlockingCollection<AudioChunk> chunks, int sampleRate, int channels)
    {
        lock (_lock)
            _sources.Add(new Source(chunks, sampleRate, channels, this, skip: _written));
    }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Audio mixer" };
        _thread.Start();
    }

    private void Run()
    {
        try
        {
            while (true)
            {
                Source[] sources;
                lock (_lock)
                    sources = _sources.ToArray();
                bool took = false;
                foreach (var source in sources)
                    took |= source.Drain();

                // Mix as far as every source still going has got to; finished ones add silence.
                var going = sources.Where(s => !s.Done).ToList();
                if (going.Count == 0 && sources.All(s => s.Buffered == 0))
                    break;
                long frames = going.Count > 0 ? going.Min(s => (long)s.Buffered) : sources.Max(s => (long)s.Buffered);
                if (frames > 0)
                {
                    Emit(sources, (int)Math.Min(frames, SampleRate / 10));
                    continue;
                }
                if (!took)
                    going[0].Wait(20);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Mixing the sound failed: {ex.Message}");
        }
        finally
        {
            Chunks.CompleteAdding();
        }
    }

    private void Emit(Source[] sources, int frames)
    {
        int count = frames * Channels;
        var mix = new int[count];
        foreach (var source in sources)
            source.TakeInto(mix, frames);
        var pcm = new byte[count * 2];
        for (int i = 0; i < count; i++)
        {
            short sample = (short)Math.Clamp(mix[i], short.MinValue, short.MaxValue);
            pcm[i * 2] = (byte)sample;
            pcm[i * 2 + 1] = (byte)(sample >> 8);
        }
        var time = TimeSpan.FromTicks(_written * 10_000_000 / SampleRate);
        _written += frames;
        Chunks.Add(new AudioChunk(pcm, time, TimeSpan.FromTicks(frames * 10_000_000L / SampleRate)));
    }

    /// <summary>One source's sound, converted to the mix's format, waiting to be mixed.</summary>
    private sealed class Source(BlockingCollection<AudioChunk> chunks, int sampleRate, int channels, AudioMixer mixer, long skip)
    {
        private readonly int _channels = channels;
        private readonly double _step = (double)sampleRate / mixer.SampleRate; // input frames per output frame
        private readonly List<short> _input = new();   // input frames not yet used, interleaved
        private readonly List<short> _output = new();  // converted frames, interleaved in the mix's channels
        /// <summary>Where the next output frame falls in <see cref="_input"/>, in input frames.</summary>
        private double _position = skip * ((double)sampleRate / mixer.SampleRate);
        private long _dropped; // input frames before _input[0]

        public bool Done { get; private set; }
        public int Buffered => _output.Count / mixer.Channels;

        /// <summary>Takes what has arrived; true if anything did.</summary>
        public bool Drain()
        {
            bool any = false;
            while (chunks.TryTake(out var chunk))
            {
                Append(chunk.Pcm);
                any = true;
            }
            if (chunks.IsCompleted)
                Done = true;
            if (any)
                Convert();
            return any;
        }

        public void Wait(int milliseconds)
        {
            if (chunks.TryTake(out var chunk, milliseconds))
            {
                Append(chunk.Pcm);
                Convert();
            }
        }

        private void Append(byte[] pcm)
        {
            for (int i = 0; i + 1 < pcm.Length; i += 2)
                _input.Add((short)(pcm[i] | pcm[i + 1] << 8));
        }

        /// <summary>Resamples and maps channels for as much input as there is.</summary>
        private void Convert()
        {
            int inputFrames = _input.Count / _channels;
            int outChannels = mixer.Channels;
            while (true)
            {
                double at = _position - _dropped;
                int i = (int)Math.Floor(at);
                if (i + 1 >= inputFrames)
                    break;
                if (i >= 0)
                {
                    double fraction = at - i;
                    for (int c = 0; c < outChannels; c++)
                    {
                        // Mono goes to every channel; extra channels are left out.
                        int from = Math.Min(c, _channels - 1);
                        double a = _input[i * _channels + from], b = _input[(i + 1) * _channels + from];
                        _output.Add((short)(a + (b - a) * fraction));
                    }
                }
                _position += _step;
            }
            // Let go of input that's behind us.
            int used = Math.Clamp((int)Math.Floor(_position - _dropped), 0, inputFrames);
            if (used > 0)
            {
                _input.RemoveRange(0, used * _channels);
                _dropped += used;
            }
        }

        /// <summary>Adds up to <paramref name="frames"/> frames into the mix (silence past what's here).</summary>
        public void TakeInto(int[] mix, int frames)
        {
            int count = Math.Min(frames * mixer.Channels, _output.Count);
            for (int i = 0; i < count; i++)
                mix[i] += _output[i];
            _output.RemoveRange(0, count);
        }
    }
}
