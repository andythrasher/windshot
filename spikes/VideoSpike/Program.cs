using System.Diagnostics;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace VideoSpike;

/// <summary>
///   VideoSpike.exe &lt;output folder&gt; [full|1080] [seconds] [fps]
/// Records the primary monitor (full) or a 1920x1080 area in its middle, with system audio (it
/// plays quiet beeps so there's something to hear), then exports a trimmed, annotated MP4 and a GIF.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string folder = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
        string mode = args.Length > 1 ? args[1] : "1080";
        double seconds = args.Length > 2 && mode != "export" ? double.Parse(args[2]) : 6;
        int fps = args.Length > 3 ? int.Parse(args[3]) : 30;
        Directory.CreateDirectory(folder);

        var screen = System.Drawing.Rectangle.FromLTRB(0, 0, GetSystemMetrics(0), GetSystemMetrics(1));
        var region = mode == "full" ? screen
            : new System.Drawing.Rectangle((screen.Width - 1920) / 2, (screen.Height - 1080) / 2, 1920, 1080);
        string recording = Path.Combine(folder, $"recording-{mode}-{fps}.mp4");
        Console.WriteLine($"Recording {region} at {fps} fps for {seconds} s -> {recording}");

        if (mode == "export")
        {
            // VideoSpike.exe <folder> export <recording.mp4> <seconds>: just the export step.
            string source = args[2];
            double total = double.Parse(args[3]);
            var watch = Stopwatch.StartNew();
            try
            {
                string report = await Exporter.ExportAsync(source, Path.Combine(folder, "export-only.mp4"), Path.Combine(folder, "export-only.gif"),
                    TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(total - 1), 30, 960, 15);
                Console.WriteLine($"Exported in {watch.Elapsed.TotalSeconds:F2} s: {report}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAILED after {watch.Elapsed.TotalSeconds:F1} s: {ex}");
                return 1;
            }
        }

        try
        {
            var beeps = Path.Combine(folder, "beeps.wav");
            WriteBeeps(beeps, seconds + 1);
            var player = new MediaPlayer { Volume = 0.15, Source = MediaSource.CreateFromUri(new Uri(beeps)) };
            player.Play();

            var audio = new LoopbackAudio();
            Console.WriteLine($"Loopback: {audio.Describe}");
            var writer = new Mp4Writer(region.Width, region.Height, fps, audio.Chunks, audio.SampleRate, audio.Channels);
            var recorder = new Recorder();
            var clock = Stopwatch.StartNew();
            var (start, end) = await recorder.RecordAsync(region, fps, TimeSpan.FromSeconds(seconds), writer, recording, audio);
            player.Pause();
            double length = (end - start) / 1e7;
            Console.WriteLine($"Recorded {length:F2} s in {clock.Elapsed.TotalSeconds:F2} s: frames arrived {recorder.Arrived}, kept {recorder.Kept} ({recorder.Kept / length:F1} fps), " +
                $"dropped by cap {recorder.DroppedByCap}, dropped busy {recorder.DroppedBusy}; video samples {writer.VideoSamples}, audio samples {writer.AudioSamples}; " +
                $"audio sound {audio.SoundFrames / (double)audio.SampleRate:F2} s, silence {audio.SilenceFrames / (double)audio.SampleRate:F2} s");
            Console.WriteLine($"Notes: {recorder.Notes}");
            Console.WriteLine($"File: {new FileInfo(recording).Length / 1e6:F1} MB");
            audio.Dispose();

            clock.Restart();
            string exported = Path.Combine(folder, $"export-{mode}-{fps}.mp4"), gif = Path.Combine(folder, $"export-{mode}-{fps}.gif");
            string report = await Exporter.ExportAsync(recording, exported, gif, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(seconds - 1), fps, 960, 15);
            Console.WriteLine($"Exported in {clock.Elapsed.TotalSeconds:F2} s: {report}");
            Console.WriteLine($"MP4 {new FileInfo(exported).Length / 1e6:F1} MB, GIF {new FileInfo(gif).Length / 1e6:F1} MB");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED: {ex}");
            return 1;
        }
    }

    /// <summary>A short beep every half second, rising in pitch: easy to line up against the video.</summary>
    private static void WriteBeeps(string path, double seconds)
    {
        const int rate = 48000;
        int frames = (int)(rate * seconds);
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + frames * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(frames * 2);
        for (int i = 0; i < frames; i++)
        {
            double t = (double)i / rate;
            int beep = (int)(t / 0.5);
            double within = t - beep * 0.5;
            double sample = within < 0.1 ? Math.Sin(2 * Math.PI * (440 + beep * 60) * t) * 0.6 : 0;
            w.Write((short)(sample * short.MaxValue));
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
