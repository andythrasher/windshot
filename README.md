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

The app lives in the tray; tick **Start with Windows** in its menu to have it there (with your
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
default), **C** crop, **V** select. **[** and **]** (or the
Size slider) and **1–8** (colors) change the selected object, or the next one drawn. Drag
handles to reshape arrows and rectangles, or a text box's corners to scale its text; double-click text to edit it.
**Crop** shows the whole canvas with the crop marked: drag its edges or corners, drag inside to
move it, or drag out a new area; Enter (or another tool) applies it and Esc cancels. Nothing is
thrown away, so picking Crop again lets you loosen it, and undo covers it. **Ctrl+Z** /
**Ctrl+Y** undo and redo, **Delete** removes, **Ctrl+C** copies, **Ctrl+S** saves. Copying or
saving closes the editor (turn that off with `CloseAfterSaveOrCopy` in the settings).

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

**Layers** (L, or the toolbar's Layers button) opens a panel listing every object, topmost
first, with the screenshot at the bottom. Click a layer to select it, drag to restack it (or
Ctrl+] and Ctrl+[, with Shift for top and bottom), use the eye to hide it (hidden layers aren't
exported), set its opacity with the slider, and double-click to rename it. Blur, highlight and
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
`%LOCALAPPDATA%\Windshot\pins`.

## Settings

Tray menu → **Settings…** (or `Windshot.exe --settings`) opens the settings window: click a
shortcut and press new keys to change it (Esc cancels, Backspace turns it off; shortcuts need
Ctrl, Alt or Win unless they're a function key or Print Screen, and ones already taken are
refused), plus Start with Windows, the magnifier, closing after copy or save, and Beautify.
Changes apply immediately.

Everything is stored in `%LOCALAPPDATA%\Windshot\settings.json` (linked from the window), which is
also hand-editable; comments and trailing commas are fine, and changes apply when you save.

- `Hotkeys`: `Capture`, `CopyText` and `ScrollingCapture`, written like `"Ctrl+Shift+2"`, `"Alt+S"` or `"PrintScreen"`
  (for Print Screen, first turn off "Use the Print screen key to open screen capture" in
  Windows Settings → Accessibility → Keyboard).
- `Capture`: `ShowMagnifier` (default `true`).
- `Editor`: per-tool sizes (1–10), colors (`"#RRGGBB"`), pixelate and text background, and
  `CloseAfterSaveOrCopy` (default `true`).
  Saved automatically when an editor closes.
- `Beautify`: `OnByDefault`, `Preset` (Sky, Sunset, Grape, Mint, Peach, Night, Graphite,
  Paper) and `Padding`.

If the file can't be read, Windshot uses defaults and keeps the broken copy as `settings.json.bad`.

Log: `%LOCALAPPDATA%\Windshot\windshot.log`.

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
- **WinUI 3 + Win2D for the editor; WinForms for tray, hotkey and overlay.** WinForms windows
  appear instantly. It's referenced via `FrameworkReference` rather than `UseWindowsForms`,
  because the latter pulls in WPF's XAML compiler, which breaks the WinUI build.
