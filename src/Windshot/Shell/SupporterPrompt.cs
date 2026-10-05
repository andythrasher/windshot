using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Windshot.Shell;

/// <summary>Asks to become a supporter when a supporter extra is used, and runs the purchase.</summary>
internal static class SupporterPrompt
{
    /// <summary>What supporting unlocks, for descriptions.</summary>
    public const string Extras = "more editor themes and color sets, the color wheel, fonts and stickers";

    private static bool _showing;

    /// <param name="feature">What was used, as the start of a sentence, e.g. "Stickers are" or "The color wheel is".</param>
    /// <returns>Whether the extras are unlocked now.</returns>
    public static async Task<bool> ShowAsync(Window window, XamlRoot root, string feature)
    {
        if (Supporter.IsUnlocked)
            return true;
        if (_showing)
            return false;
        _showing = true;
        try
        {
            string price = Supporter.Price is { } p ? $" of {p}" : "";
            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = "Become a supporter?",
                Content = new TextBlock
                {
                    Text = $"{feature} a supporter extra. Windshot stays free; a one-time purchase{price} unlocks {Extras}, and helps keep Windshot going.",
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "Become a supporter",
                CloseButtonText = "Not now",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return false;

            if (await Supporter.PurchaseAsync(window) is { } problem)
            {
                await new ContentDialog { XamlRoot = root, Title = "Couldn't finish", Content = problem, CloseButtonText = "OK" }.ShowAsync();
                return false;
            }
            return Supporter.IsUnlocked;
        }
        finally
        {
            _showing = false;
        }
    }
}
