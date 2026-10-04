# Winshot

A Shottr-style screenshot tool for Windows: Snipping Tool's polish, with an editor where
annotations stay editable objects and the canvas grows when you draw past the edge.

## Build and run

Requires the .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`). No Visual Studio needed.

```
dotnet build src/Winshot
src/Winshot/bin/Debug/net10.0-windows10.0.26100.0/win-x64/Winshot.exe
```

The app lives in the tray. **Ctrl+Shift+2** captures a region (or left-click the tray icon,
or launch the exe again while it's running). **Ctrl+Shift+3** copies the text in a region
straight to the clipboard (offline Windows OCR); in the editor, **Ctrl+Shift+C** does the same
for the whole capture.

In the editor: **A** arrow, **R** rectangle, **T** text (click, or drag a box to set the size
and wrapping width; the toolbar's text background toggle sets it on a colored box), **B** blur (**P** toggles pixelate,
the default and the safer choice for hiding text), **N** numbered steps (they renumber when
one is deleted), **S** spotlight (dims everything else), **H** highlighter (keeps its own color, yellow by
default), **V** select. **[** and **]** (or the
Size slider) and **1–8** (colors) change the selected object, or the next one drawn. Drag
handles to reshape arrows and rectangles; double-click text to edit it. **Ctrl+Z** /
**Ctrl+Y** undo and redo, **Delete** removes, **Ctrl+C** copies, **Ctrl+S** saves.

**Beautify** (toolbar toggle; its arrow picks a gradient and padding) puts the capture on a
gradient backdrop with rounded corners and a shadow, in the editor and in everything exported.
**Ctrl+P** pins the result above other windows where it was captured: drag to move, scroll to
zoom, Ctrl+scroll for opacity, Esc or middle-click to close, right-click for more.

Log: `%LOCALAPPDATA%\Winshot\winshot.log`.

## Design notes

- **Unbounded canvas.** `Editing/Document.cs` treats the screenshot as one layer at (0,0).
  Canvas bounds are the image plus any annotation that sticks out (with a margin), so
  "reverse crop" falls out of the model. Export renders those bounds. Added area is filled
  per side with the dominant color of the image edge beside it (`EdgeColors`), preferring the
  stretch next to whatever overhangs; sides without a clear background color stay transparent.
- **Physical pixels everywhere on the capture side.** The process is Per-Monitor-V2 DPI aware,
  the desktop is snapshotted once, and each monitor gets its own overlay window in its own
  pixel space, which keeps mixed-DPI setups from breaking.
- **WinUI 3 + Win2D for the editor; WinForms for tray, hotkey and overlay.** WinForms windows
  appear instantly. It's referenced via `FrameworkReference` rather than `UseWindowsForms`,
  because the latter pulls in WPF's XAML compiler, which breaks the WinUI build.
