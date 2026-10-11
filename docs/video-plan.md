# Video recording and annotation: plan

Status: phase 1 (1.3) submitted 2026-10-10; phase 2 (1.4, timing and fades) built and tested 2026-10-11; phase 3 (1.5, motion and the Zoom layer) built and tested 2026-10-11. Phase 1 records the selected area; a clicked window is recorded as its rectangle, not followed if it moves. Decided so far: system audio (no microphone yet), MP4 and GIF
export, editing stays in the existing editor with a timeline added, trimming by dragging only.

## How it fits the current code

- The editor's `Document` has one base layer, `Image` (a `CanvasBitmap`), and `Compose` stacks the
  annotations over it as a chain of Win2D command lists. For video, the base layer becomes the
  frame at the playhead, and `Compose` takes a time, so each layer can work out whether it's
  showing, how faded it is, and where it has moved to. Everything else (beautify, crop, styles,
  effects that act on what's beneath them) works per frame unchanged.
- Annotations gain timing: `Start`/`End` (default: the whole clip), `FadeIn`/`FadeOut`, and a
  `Motion` preset. Hit testing and handles only consider layers showing at the playhead.
- Screenshots are taken with GDI (`CopyFromScreen`). Video uses Windows.Graphics.Capture instead:
  GPU frames, a monitor or a window (a window is followed as it moves), the cursor optional.

## Recording

- **Start:** a new shortcut, Ctrl+Shift+5 (like macOS's recording shortcut), plus a tray item, a
  fourth row in Settings and the welcome window. The existing overlay picks a region, a window or
  a whole monitor.
- **While recording:** a thin border around the area and a small bar beside it (elapsed time,
  system audio on/off, Stop), both kept out of the recording like the scrolling capture panel
  (`WDA_EXCLUDEFROMCAPTURE`). The shortcut again, or Stop, finishes; Esc cancels.
- **Audio:** system sound by WASAPI loopback, on by default, remembered. No microphone in v1
  (that would need a Store capability and a privacy policy change).
- **Settings:** frame rate (30 default, 60), cursor on/off. Region recordings stay on one monitor.
- **Storage:** written straight to an MP4 (H.264 + AAC, the hardware encoder) in the app's data
  folder (`Recordings`), deleted when its editor closes; leftovers from a crash are cleaned up at
  the next start. Recordings never leave the PC unless you save or copy them.

## Editing

- The same editor window. A **timeline** appears along the bottom: play/pause, the time, a strip
  of thumbnails, and a playhead to drag. Drag the strip's ends to trim; what's cut away is dimmed,
  never thrown away, like crop.
- **Layer timing:** selecting a layer shows its bar under the strip; drag its ends to set when it
  appears and disappears. Same gesture as trimming, no keyframes.
- **Layer options (secondary menu):** Fade in, Fade out (off / quick / slow), and Motion: none,
  zoom in, zoom out, pan left/right/up/down, applied across the layer's time (Ken Burns).
- **Zoom layer:** a new adjustment layer, drawn like a spotlight: during its time the view eases
  in to that area and back out. The most useful effect for screen recordings.
- **Video options (secondary menu, nothing selected):** mute audio, export size (original, 1080p,
  720p).
- **Blur doesn't follow moving content.** Say so in its tooltip/options when the document is a
  video, rather than imply that redaction is safe.
- What changes for other commands: Copy exports and copies the file (it pastes into Slack, Teams,
  Explorer); Copy text reads the current frame; Pin is hidden; Crop applies to every frame.
  Closing an unsaved recording asks first (losing a recording hurts more than losing a screenshot).

## Export

- **MP4:** H.264 + AAC at the chosen size. **GIF:** WIC's GIF encoder, 15 fps, 960 px wide by default,
  with a size warning past ~30 s.
- Save (Ctrl+S) goes to Videos\Screen Recordings (where Snipping Tool puts them), with its own
  folder setting; Save as offers MP4 or GIF.
- A progress bar with Cancel in the editor; the editor stays usable, and "close after saving"
  waits until it's done.

## The risky part: getting frames in and out without ffmpeg

Bundling ffmpeg would add ~80 MB to the package and licensing obligations, so the plan is the
video support built into Windows. Two candidate routes:

1. **Windows.Media.Editing (MediaComposition):** trimming, preview and rendering to a file are
   built in, with annotations drawn by a custom video effect using Win2D. Catch: a video effect
   must be a registered WinRT class, which is awkward from .NET (and for the unpackaged build).
2. **Media Foundation directly:** SourceReader to decode frames (exact seeking), Win2D to compose,
   SinkWriter to encode; MediaPlayer's frame server for smooth preview. More plumbing, full control.

WASAPI loopback and Media Foundation are COM, and the release build is trimmed, which has bitten
before. So step 0 is a spike that proves the pipeline before any UI.

## Step 0 results (2026-10-10)

`spikes/VideoSpike` records a region with system audio, then reads it back, trims it, draws an
annotation that changes over time on each frame, and writes MP4 and GIF. It runs in a trimmed,
self-contained build like Windshot's release build. 4K monitor at 150%, NVIDIA GPU.

**Verdict: route 2 works. We use Windows' own media stack, without ffmpeg and without
MediaComposition.** Pieces, all working in the trimmed build:

- **Capture:** Windows.Graphics.Capture.
  - A monitor item via `IGraphicsCaptureItemInterop`, called through its vtable.
  - Frames cropped with Win2D on the GPU.
  - Borderless capture was allowed without a prompt (unpackaged). `MinUpdateInterval` works.
- **System audio:** WASAPI loopback, called through vtables. Built-in COM interop is gone in
  trimmed builds, so every COM call is made through function pointers (see `Com.cs`).
  - Silence is filled from the clock, so the audio is continuous even when nothing plays.
  - Samples share the frames' QPC clock.
- **Encoding:** `MediaStreamSource` → `MediaTranscoder`.
  - Uses the hardware H.264 encoder plus AAC, and takes Win2D render targets directly.
  - Output is constant 30 or 60 fps, with the transcoder filling the gaps.
- **Reading:** Media Foundation `IMFSourceReader`, through vtables. It decodes to RGB32, seeks
  to the keyframe, then decodes forward to the exact frame.
- **GIF:** WIC's encoder through `BitmapEncoder`, with loop and per-frame delay metadata.

Measured:

| Test | Recording | Export (trim + annotate + MP4 + GIF) |
|---|---|---|
| 1080p region, 30 fps | 29.3 fps, nothing dropped, 3.0 MB / 6 s | 49 fps |
| 1080p region, 60 fps | every frame delivered (~51/s), 4.4 MB | 55 fps |
| 4K screen, 30 fps | 29.1 fps, nothing dropped, 12.8 MB | 15 fps (decode-bound: 25 fps) |
| 4K screen, 60 fps | every frame delivered (~49/s), 24 MB | 17 fps |

Checked with ffprobe and by looking at frames:
- **Streams:** H.264 + AAC 48 kHz stereo, exact durations. The trimmed export is 4.000 s.
- **Audio after trimming:** beeps in the export are exactly 1.0 s earlier, with no drift.
- **Frames:** the right way up, and the annotation follows the clock.

Learned, to carry into phase 1:
1. **Keep each `MediaStreamSample` referenced until `Processed`.** Otherwise the GC can collect
   it first, the handler never runs, and that render target is lost. The export stalled once
   this way.
2. **Decoded RGB32 has alpha 0.** Force it to 255 on read, or Win2D treats frames as transparent
   when scaling or blending (the GIF smeared).
3. **WGC sends frames only when something changes, with jitter.** Cap the rate to a grid of 1/fps
   slots, not "at least 1/fps since the last kept frame", which halved 30 fps to 16.
4. **4K export is limited by CPU decoding.** Next: hardware decoding into D3D11 surfaces
   (a DXGI device manager on the source reader), handed to Win2D without a CPU copy.
   Smooth preview in the editor should use MediaPlayer's frame server, with the source reader
   for exact scrubbing and export.
5. **Make GIFs ourselves.** WIC stores every frame whole: 12–17 MB for 4 s at 960 px,
   15 fps, of busy video. A small encoder of our own can store only the pixels that changed
   (transparent elsewhere), with one palette and optional dithering. That's how screen-recording
   GIFs stay small. About 300 lines, no dependency.
6. **Use cumulative rounding for GIF delays,** alternating 6 and 7 hundredths of a second for
   15 fps. Rounding each frame to 7 stretched 4.0 s to 4.2 s.
7. **Still to check:** audio/video sync against an on-screen flash, and window capture.
   Also the packaged build, where borderless capture may need the
   `graphicsCaptureWithoutBorder` capability.

## Phases (each one a release)

0. **Spike** (no UI): record a region with system audio to MP4; read it back frame-accurately,
   draw a Win2D annotation on each frame, write MP4 and GIF; in the trimmed release build; check
   4K at 30 and 60 fps. Pick route 1 or 2.
1. **1.3, Record and annotate:** shortcut, recording bar, the timeline (play, scrub, trim ends),
   annotations over the whole clip, MP4/GIF export, save/copy, mute, the close prompt. Privacy
   policy, README, site and Store listing updates.
2. **1.4, Timing and fades:** layer bars on the timeline, fade in/out.
3. **1.5, Motion:** Ken Burns presets for layers, the Zoom layer. As built: fades and motion moved
   into one Animate menu (the inline pickers overflowed the options bar). Motion starts from where
   the layer was placed and eases across its time; a selected layer holds still while paused so its
   handles line up. The Zoom layer is a camera: `Document.Camera` maps the zoomed area onto the card,
   scaling geometrically; the editor draws chrome and maps the pointer through it, and turns it off
   while a zoom is selected or drawn (or while cropping) so the area can be seen.

## Not in scope (for now)

Microphone, webcam, pausing a recording, cutting out the middle of a clip, keyframes, blur that
tracks moving content, recording across monitors, pinning video.
