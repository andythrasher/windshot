using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Windshot.Editing;

/// <summary>Mica, tinted with a theme's color: one tint for light mode and one for dark.</summary>
internal sealed class TintedMicaBackdrop(Color light, Color dark) : SystemBackdrop
{
    private MicaController? _controller;
    private SystemBackdropConfiguration? _configuration;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(target, xamlRoot);
        _configuration = GetDefaultSystemBackdropConfiguration(target, xamlRoot);
        _controller = new MicaController();
        ApplyTint();
        _controller.SetSystemBackdropConfiguration(_configuration);
        _controller.AddSystemBackdropTarget(target);
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnDefaultSystemBackdropConfigurationChanged(target, xamlRoot);
        ApplyTint(); // light and dark mode have their own tints
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        base.OnTargetDisconnected(target);
        _controller?.RemoveSystemBackdropTarget(target);
        _controller?.Dispose();
        _controller = null;
    }

    private void ApplyTint()
    {
        if (_controller is null || _configuration is null)
            return;
        var tint = _configuration.Theme == SystemBackdropTheme.Dark ? dark : light;
        // Strong enough that the theme's color reads, while the desktop still shows through a little.
        _controller.TintColor = tint;
        _controller.TintOpacity = 0.8f;
        _controller.LuminosityOpacity = 0.9f;
        _controller.FallbackColor = tint; // when Mica is off, e.g. in battery saver
    }
}
