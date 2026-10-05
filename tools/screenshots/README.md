# Store screenshots

Scripts that make the Microsoft Store screenshots in `store/screenshots` (3840×2160 PNG). They
capture a fictional app ("Lumen", drawn by `make-demo.ps1`) shown full-screen behind Windshot,
never a real desktop, and drive the installed Windshot with simulated mouse and keyboard input.
Each grab of Windshot's own window is placed on a branded background under a headline
(`Compose` in `lib-shots.ps1`).

1. `powershell -File tools/screenshots/make-demo.ps1` draws the demo app.
2. Install the current build (`tools/install.ps1`). Before each run script, restart Windshot so
   its log ends with a fresh "Started" line; the scripts refuse to send input otherwise, and
   stop whenever the editor isn't visible and in front.
3. Run, one at a time, with hands off the mouse and keyboard (each takes ~15 seconds):
   - `run-editor.ps1`: 01-annotate, 03-layers, 05-beautify
   - `run-overlay-pin.ps1`: the capture overlay and a pin (raw grabs; see below)
   - `run-scroll.ps1`: 04-scrolling
4. The overlay and pin grabs (`raw-overlay.png`, `raw-pin.png`) are cropped to the demo app
   (850, 320, 2340 × 1450) before composing as 02-capture and 06-pin; 07-settings is a grab of
   the settings window (`Windshot.exe --settings`).

Assumes a 3840×2160 display at 150% scale with the taskbar at the bottom, light theme.
Copy the finished `0*.png` files to `store/screenshots`.
