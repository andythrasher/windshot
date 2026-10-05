# Store screenshots

Scripts that make the Microsoft Store screenshots in `store/screenshots` (3840×2160 PNG). They
capture a fictional app ("Lumen", drawn by `make-demo.ps1`) shown full-screen behind Windshot,
never a real desktop, and drive the installed Windshot with simulated mouse and keyboard input.
Each grab of Windshot's own window is placed on a branded background under a headline
(`Compose` in `lib-shots.ps1`).

1. `powershell -File tools/screenshots/make-demo.ps1` draws the demo app.
2. Install the current build (`tools/install.ps1`) and close any editors.
3. `powershell -File tools/screenshots/run-all.ps1` makes 01-annotate, 02-shapes, 04-layers,
   05-scrolling, 06-beautify and 07-themes, with hands off the mouse and keyboard for about two
   minutes. Each run gets known settings (07-themes: the Moss theme and colors, Segoe Print) and
   a fresh Windshot; your settings are put back afterwards. `-Runs shapes,themes` redoes some.
   The scripts refuse to send input unless the log shows a fresh start, and stop whenever the
   editor isn't visible and in front.
4. 03-capture, 08-pin and 09-settings come from `run-overlay-pin.ps1` (raw grabs, cropped to the
   demo app at 850, 320, 2340 × 1450 before composing) and a grab of the settings window
   (`Windshot.exe --settings`). They didn't change for 1.1.

Assumes a 3840×2160 display at 150% scale with the taskbar at the bottom, light theme.
Copy the finished `0*.png` files to `store/screenshots`.