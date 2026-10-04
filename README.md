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
or launch the exe again while it's running).

In the editor: **A** arrow, **R** rectangle, **T** text, **V** select. **[** and **]** (or the
Size slider) change the selected object, or the next one drawn. Drag handles to reshape arrows
and rectangles; double-click text to edit it. **Delete** removes, **Ctrl+C** copies, **Ctrl+S** saves.

Log: `%LOCALAPPDATA%\Winshot\winshot.log`.

## Design notes

- **Unbounded canvas.** `Editing/Document.cs` treats the screenshot as one layer at (0,0).
  Canvas bounds are the image plus any annotation that sticks out (with a margin), so
  "reverse crop" falls out of the model. Export renders those bounds; the extra area is transparent.
- **Physical pixels everywhere on the capture side.** The process is Per-Monitor-V2 DPI aware,
  the desktop is snapshotted once, and each monitor gets its own overlay window in its own
  pixel space, which keeps mixed-DPI setups from breaking.
- **WinUI 3 + Win2D for the editor; WinForms for tray, hotkey and overlay.** WinForms windows
  appear instantly. It's referenced via `FrameworkReference` rather than `UseWindowsForms`,
  because the latter pulls in WPF's XAML compiler, which breaks the WinUI build.
