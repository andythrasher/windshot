using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Winshot.Capture;
using Winshot.Editing;
using Winshot.Shell;
using Keys = System.Windows.Forms.Keys;

namespace Winshot;

/// <summary>
/// Tray-resident app: no window at startup, a global hotkey opens the capture overlay,
/// and each capture opens its own editor window.
/// </summary>
public partial class App : Application
{
    private static EventWaitHandle? _captureSignal;
    private HotkeyWindow? _hotkeys;
    private TrayIcon? _tray;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => Log.Write($"Unhandled: {e.Exception}");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Single instance: launching again just asks the running instance to capture,
        // so a pinned taskbar icon or a shortcut works as a capture button.
        _captureSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Winshot.Capture", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            _captureSignal.Set();
            Exit();
            return;
        }

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        ThreadPool.RegisterWaitForSingleObject(_captureSignal,
            (_, _) => dispatcher.TryEnqueue(StartCapture), null, Timeout.Infinite, executeOnlyOnce: false);

        // Stay alive with zero windows open; we only exit from the tray menu.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;

        _hotkeys = new HotkeyWindow();
        string? captureHotkey = Register(Keys.D2, "Ctrl+Shift+2", StartCapture);
        string? copyTextHotkey = Register(Keys.D3, "Ctrl+Shift+3", StartTextCapture);
        _tray = new TrayIcon(StartCapture, StartTextCapture, Quit, captureHotkey, copyTextHotkey);
        Log.Write($"Started; hotkeys capture={captureHotkey ?? "taken"} text={copyTextHotkey ?? "taken"}");
    }

    /// <returns>The shortcut's label, or null if another app already owns it.</returns>
    private string? Register(Keys key, string label, Action handler) =>
        _hotkeys!.TryRegister(HotkeyModifiers.Control | HotkeyModifiers.Shift, key, handler) ? label : null;

    /// <summary>Select a region and copy its text, without opening the editor.</summary>
    private void StartTextCapture()
    {
        try
        {
            CaptureSession.Begin(image => _ = CopyTextAsync(image), hint: "Select text to copy");
        }
        catch (Exception ex)
        {
            Log.Write($"Text capture failed: {ex}");
        }
    }

    private static async Task CopyTextAsync(CapturedImage image)
    {
        string message = await TextRecognizer.CopyToClipboardAsync(image.Pixels, image.Width, image.Height, image.Scale);
        Log.Write($"OCR: {message}");
        Hud.Show(message, image.DesktopBounds, image.Scale);
    }

    private void StartCapture()
    {
        try
        {
            CaptureSession.Begin(OpenEditor);
        }
        catch (Exception ex)
        {
            Log.Write($"Capture failed: {ex}");
        }
    }

    internal static void OpenEditor(CapturedImage image)
    {
        // Often runs inside a WinForms mouse handler, which would otherwise swallow exceptions.
        try
        {
            new EditorWindow(image).Activate();
        }
        catch (Exception ex)
        {
            Log.Write($"Opening editor failed: {ex}");
        }
    }

    private void Quit()
    {
        _hotkeys?.Dispose();
        _tray?.Dispose();
        Exit();
    }
}
