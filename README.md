# Windshot

A Shottr-style screenshot tool for Windows: Snipping Tool's polish, with an editor where
annotations stay editable objects and the canvas grows when you draw past the edge.

## Build and run

Requires the .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`). No Visual Studio needed.

To install (or update) it for your account, in `%LOCALAPPDATA%\Programs\Windshot` with a Start
menu shortcut, run:

```
powershell -ExecutionPolicy Bypass -File tools/install.ps1
```

It builds a self-contained Release version (no .NET install needed), closes any running
Windshot (refusing if an editor is open), installs it and starts it. If Start with Windows is on,
it now starts the installed copy. `-Uninstall` removes it again; settings and pins in
`%LOCALAPPDATA%\Windshot` stay either way.

For development, build and run the Debug version in place:

```
dotnet build src/Windshot
src/Windshot/bin/Debug/net10.0-windows10.0.26100.0/win-x64/Windshot.exe
```

### Microsoft Store package

```
powershell -ExecutionPolicy Bypass -File tools/package.ps1
```

builds `artifacts\store\Windshot_<version>_x64.msix` for upload to Partner Center, which signs
it. It uses the shared Windows App SDK runtime (the Store installs it alongside) rather than
carrying its own. The identity in `src/Windshot/Package.appxmanifest` (`Identity` Name and
Publisher, `PublisherDisplayName`) must match Partner Center's Product identity page, and the
version must go up for each submission. Logos come from `tools/make-store-assets.ps1`.

`-TestInstall` also installs it on this PC as "Windshot.Dev" (needs Developer Mode, since it's
unsigned), and `-Uninstall` removes that. It stops the unpackaged copy first, since both want
the same hotkeys. The packaged app differs from the unpackaged one in two ways: Start with
Windows is the package's startup task (Settings > Apps > Startup), and settings, pins and the
log live in `%LOCALAPPDATA%\Packages\<package family name>\LocalState`.

The app lives in the tray. Started by hand (not at sign-in), it opens a welcome window that says
so, with the shortcuts, the editor theme and Start with Windows; its "Show this when Windshot
starts" box (or Settings) turns that off, and `--no-welcome` skips it for scripts. The very first
start also turns on Start with Windows. Tick **Start with Windows** in the tray menu to have it there (with your
pins) after signing in. That's the usual per-user startup entry, so it also shows in Task
Manager's Startup apps, and turning it off there is reflected in the menu. **Ctrl+Shift+2** captures: drag a region, or click to capture the
window under the cursor (click the desktop for the whole monitor). On Windows 11, window
captures keep the window's rounded corners: what showed through them is made transparent, and
stays transparent when copied (as PNG), saved or pinned. Left-clicking the tray icon,
or launching the exe again while it's running, does the same.

While capturing, a magnifier follows the cursor with the color under it. **C** copies that
color as hex, **R** toggles a smart ruler (measures the same-colored run through the cursor,
e.g. a button's width and height; click to copy), **M** hides the magnifier, and the arrow
keys nudge the cursor one pixel. **Ctrl+Shift+3** copies the text in a region
straight to the clipboard (offline Windows OCR); in the editor, **Ctrl+Shift+C** does the same
for the whole capture.

In the editor: **A** arrow, **R** rectangle, **T** text (click, or drag a box to set the size
and wrapping width; the toolbar's text background toggle sets it on a colored box), **B** blur (**P** toggles pixelate,
the default and the safer choice for hiding text), **N** numbered steps (they renumber when
one is deleted), **S** spotlight (dims everything else), **H** highlighter (keeps its own color, yellow by
default), **Z** zoom (recordings only), **C** crop, **V** select. **[** and **]** (or the
Size slider) and **1–8** (colors) change the selected object, or the next one drawn. Drag
handles to reshape arrows and rectangles, or a text box's corners to scale its text; double-click text to edit it.
**Style** on the options bar gives arrows, shapes, text, steps and images an outline (white or
black), a soft shadow, and rounder corners (rectangles, images and text backgrounds); each tool
remembers its style. **Insert image** on the toolbar adds a PNG, JPEG or WebP picture as a layer
(or paste one with Ctrl+V, or drop image files on the editor): it comes in at its own size, or
shrunk to fit the screenshot, and its corners keep its proportions unless Shift is held.
Drag the round handle above a selected object to rotate it (double-click that handle to straighten it).
Hold **Shift** while dragging for squares, circles and even triangles (an existing shape keeps its
proportions), arrows, highlights and polygon sides at 45° steps, rotation in 15° steps, and moves
straight across or up and down.
**Crop** shows the whole canvas with the crop marked: drag its edges or corners, drag inside to
move it, or drag out a new area; Enter (or another tool) applies it and Esc cancels. Nothing is
thrown away, so picking Crop again lets you loosen it, and undo covers it. **Ctrl+Z** /
**Ctrl+Y** undo and redo, **Delete** removes, **Ctrl+C** copies, **Ctrl+S** saves straight to
*Pictures\Screenshots* (Settings can change the folder, or turn that off to be asked each time),
and **Ctrl+Shift+S** asks where to save. Copying or saving closes the editor (turn that off in
Settings).

**Ctrl+Shift+4** starts a **scrolling capture**: select the scrolling area (or click a window),
then scroll it yourself, with the wheel, keyboard or scrollbar. Windshot grabs frames as it moves
and stitches them into one tall image. Sticky headers, footers and sidebars are handled; a
panel below the area shows progress (if it says it lost track, you scrolled too fast, so back up a
little). Or press **Auto-scroll** on the panel: Windshot scrolls for you and opens the result
when the page stops moving. It first sends wheel messages straight to the window, leaving
your mouse alone; apps that ignore those get real wheel input, with the mouse parked over the
area and put back afterwards (move the mouse to pause). Press **Done**, Enter, or Ctrl+Shift+4 again to open the result. Selecting just
the scrolling part of a window (not its sidebar or toolbar) gives the cleanest result. Tall
captures open at the top, fitted to the window's width; there the wheel scrolls and
Ctrl+wheel zooms.

**Ctrl+Shift+5** records a video: select an area (or click a window), and everything there is
recorded, with the sound the PC plays, until you press Stop on the small bar beside it, or the
shortcut again (Cancel or Esc throws it away). The bar's sound switch mutes the recording. The
outline and bar are kept out of the recording, and so is Windows' yellow border (the Store app
asks once for permission to leave it off). Recordings go straight into the editor, where a
timeline along the bottom plays, scrubs and trims them: drag the ends of the strip (what's
trimmed is dimmed, not deleted); Space plays and pauses, the arrow keys step a frame, Home and End
jump to the ends. Everything you draw sits on every frame, and beautify and crop apply throughout.
Each layer can show for just part of the recording: select it, and its bar appears under the
strip; drag its ends to set when it appears and goes, or drag the bar to move it. Its ends snap to
other layers' starts and ends (ticks in the lane mark them) and to where the playhead was, with a
guide line; the trim handles snap the same way, so you can play to a moment, pause, and trim to it.
A light band along the top of the strip shows where the other layers are, brighter where they
overlap. New layers
start at the playhead. The options bar's **Animate** menu sets how a layer comes in and goes out: at
once, or Quick or Slow with a fade, a pop (it grows in with a little overshoot), a wipe right or
left, a wipe in from the edges or out from the center, or a radial wipe clockwise or
counterclockwise; wipes can have sharp or feathered edges, and going out, a wipe continues the same
way (a wipe right clears it left to right). New highlights wipe right, like a pen stroke. Effects
can't pop, and spotlights only fade. Motion (a slow zoom in or out, or a pan left, right, up or down, across the layer's time,
starting from where it was placed; a selected layer holds still so it can be edited) and **Show for
the whole video** are there too; selecting a layer that isn't showing moves the playhead to it.
While paused, the selected layer shows in full, without its transitions or motion, so it can be
edited; playing or scrubbing shows it animating. Other layers partway in or out at the playhead get
a faint dashed outline, and can be clicked even where they don't show yet. The **Zoom** tool
(Z) marks an area for the video to zoom in to: during the layer's time the view eases in until the
area (widened to the video's shape) fills the frame, annotations included, then eases back out. Its
Zoom in and Zoom out settings are how long that takes, and it lasts four seconds to start with.
While a zoom is selected or being drawn, the view stays zoomed out to show its area.
With nothing selected, the options bar has the sound switch, the format Save and Copy make (MP4,
or an animated GIF) and a size limit. Save puts videos in *Videos\Screen Recordings* (Settings
has its own folder, frame rate and pointer settings), and Copy copies the video as a file, which
pastes into chats, emails and folders. Closing an unsaved recording asks first. While editing, the
recording waits in `%LOCALAPPDATA%\Windshot\Recordings`, and it's deleted when the editor closes.

**Layers** (L, or the toolbar's Layers button) opens a panel listing every object, topmost
first, with the screenshot at the bottom. Click a layer to select it, drag to restack it (or
Ctrl+] and Ctrl+[, with Shift for top and bottom), use the eye to hide it (hidden layers aren't
exported), set its opacity with the slider, and double-click to rename it. Ctrl-click or
Shift-click rows (or Ctrl-click objects on the canvas) to select several layers: then the color,
the opacity slider and Delete act on all of them, and dragging one moves them together. Blur, highlight and
spotlight work like adjustment layers: they change everything beneath them, so a blur above an
arrow blurs the arrow too. New ones start just above the screenshot. Step numbers follow the
order they were placed, not the stacking. Hiding the screenshot exports just the annotations.

**Zoom** with the scroll wheel (around the cursor) or **Ctrl+=** / **Ctrl+−**; **Ctrl+0** fits the
window and **Ctrl+1** shows actual pixels. Pan with **Space+drag** or a middle-button drag. The
zoom readout in the corner switches between fit and 100% when clicked. Above 150% pixels are
drawn with hard edges for inspection; exports are unaffected.

**Beautify** (toolbar toggle; its arrow picks a gradient and padding) puts the capture on a
gradient backdrop with rounded corners and a shadow, in the editor and in everything exported.
**Ctrl+P** pins the result above other windows where it was captured: drag to move, scroll to
zoom, Ctrl+scroll for opacity, Esc or middle-click to close, right-click for more. Pins stay
put across restarts (and crashes) until you close them; they're kept in
`%LOCALAPPDATA%\Windshot\pins`. A pin that can't be read is moved to `pins\unreadable` instead
of being retried at every start.

## Settings

Tray menu → **Settings…** (or `Windshot.exe --settings`) opens the settings window: click a
shortcut and press new keys to change it (Esc cancels, Backspace turns it off; shortcuts need
Ctrl, Alt or Win unless they're a function key or Print Screen, and ones already taken are
refused), plus Start with Windows, the magnifier, closing after copy or save, keeping copies
out of clipboard history, and Beautify.
Changes apply immediately.

Everything is stored in `%LOCALAPPDATA%\Windshot\settings.json` (linked from the window), which is
also hand-editable; comments and trailing commas are fine, and changes apply when you save.

- `Hotkeys`: `Capture`, `CopyText`, `ScrollingCapture` and `Record`, written like `"Ctrl+Shift+2"`, `"Alt+S"` or `"PrintScreen"`
  (for Print Screen, first turn off "Use the Print screen key to open screen capture" in
  Windows Settings → Accessibility → Keyboard).
- `ShowWelcome` (default `true`): the welcome window when Windshot is started by hand.
- `Capture`: `ShowMagnifier` (default `true`).
- `Editor`: per-tool sizes (1–10), colors (`"#RRGGBB"`), pixelate and text background, and
  `CloseAfterSaveOrCopy` (default `true`).
  Saved automatically when an editor closes.
- `Video`: `FrameRate` (30 or 60), `Cursor`, `Sound`, `Folder` (empty: Videos\Screen Recordings),
  `Format` (`"MP4"` or `"GIF"`) and `MaxHeight` (0 keeps the recording's size).
- `Beautify`: `OnByDefault`, `Preset` (Sky, Sunset, Grape, Mint, Peach, Night, Graphite,
  Paper) and `Padding`.
- `Clipboard`: `KeepOutOfHistory` (default `false`) leaves copies out of Win+V history and
  cloud clipboard sync.

If the file can't be read, Windshot uses defaults and keeps the broken copy as `settings.json.bad`.

Log: `%LOCALAPPDATA%\Windshot\windshot.log` (past 1 MB it becomes `windshot.old.log`). It
records what happened (captures, shortcuts, errors), never screenshot contents or copied text.

The app icon is generated by `tools/make-icon.ps1` (each size drawn separately so small
sizes stay crisp); rerun it after tweaking the design. Toolbar icons are
[Fluent System Icons](https://github.com/microsoft/fluentui-system-icons) (MIT, see
`THIRD-PARTY-NOTICES.md`), extracted as vector paths by `tools/make-fluent-icons.ps1`;
to add one, add its name to the script and rerun it. The open and grabbing hand cursors (for
panning and dragging pins; Windows' only built-in hand is the link pointer) are drawn from the
same icons by `tools/make-cursors.ps1`.

## Design notes

- **Unbounded canvas.** `Editing/Document.cs` treats the screenshot as one layer at (0,0).
  Canvas bounds are the image plus any annotation that sticks out (with a margin), so
  "reverse crop" falls out of the model. Export renders those bounds. Added area is filled
  per side with the dominant color of the image edge beside it (`EdgeColors`), preferring the
  stretch next to whatever overhangs; sides without a clear background color stay transparent.
- **Scrolling capture stitches by rows.** `Capture/ScrollStitcher.cs` reduces each row of
  the columns that changed between frames to a coarse signature (strip brightnesses), since
  browsers redraw scrolled content with slight differences; the offset where the most rows
  look the same wins, if most of the rows that should still be visible agree and no other
  offset comes close. Auto-scroll waits for each smooth-scroll animation to settle. Rows that
  stayed put at the bottom count as a footer, added once at the end. The border and panel are
  excluded from screen capture (`WDA_EXCLUDEFROMCAPTURE`), so they never land in a frame.
- **Layers render bottom-up.** `Document.Compose` builds the stack as a chain of Win2D command
  lists: shapes and text draw over what's beneath them, and effects take it as their input
  (`Annotation.ApplyTo`). Opacity blends each layer's result with what was beneath it.
- **Physical pixels everywhere on the capture side.** The process is Per-Monitor-V2 DPI aware,
  the desktop is snapshotted once, and each monitor gets its own overlay window in its own
  pixel space, which keeps mixed-DPI setups from breaking.
- **Video with Windows' own media stack, no ffmpeg.** Recording uses Windows.Graphics.Capture
  (GPU frames, cropped by Win2D) and WASAPI loopback for the sound, into `MediaStreamSource` →
  `MediaTranscoder` (the hardware H.264 encoder). The editor plays recordings with `MediaPlayer`'s
  frame server, which copies each frame into the bottom layer; export reads frames exactly with
  Media Foundation's source reader and draws the document over each (`Editing/VideoExport.cs`).
  The release build is trimmed, which removes .NET's built-in COM interop, so WASAPI and Media
  Foundation are called through their vtables (`Video/Com.cs`). GIFs come from our own encoder
  (`Video/GifWriter.cs`), which stores only what changed between frames. `spikes/VideoSpike` is
  the experiment that proved the approach; `docs/video-plan.md` has the plan and what it found.
- **WinUI 3 + Win2D for the editor; WinForms for tray, hotkey and overlay.** WinForms windows
  appear instantly. It's referenced via `FrameworkReference` rather than `UseWindowsForms`,
  because the latter pulls in WPF's XAML compiler, which breaks the WinUI build.

## License

Windshot's code is released under the [MIT License](LICENSE). It includes third-party
material under its own licenses; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

### The Windshot name and icon

The MIT License covers the code, not the Windshot name or icon. Those identify the official
app on windshot.app and the Microsoft Store, so please don't use them for a fork or a build you
distribute, or in a way that suggests it's the official app or endorsed by it. Give your version
its own name and icon; saying it's "based on Windshot" is fine and welcome.
