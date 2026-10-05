using Windows.Services.Store;

namespace Windshot;

/// <summary>
/// The supporter add-on: a one-time Microsoft Store purchase that unlocks the extra themes,
/// color sets, the color wheel, fonts and stickers. Nothing else depends on it.
/// Windshot itself makes no network connections: the license check and the purchase both
/// go through Microsoft Store, which keeps the license on the PC for offline use.
/// </summary>
internal static class Supporter
{
    /// <summary>The add-on's Store ID in Partner Center.</summary>
    private const string AddOnStoreId = "9NJ2V9GCKP9F";

    /// <summary>The last license check's answer, remembered so supporters don't see a locked editor while the Store answers.</summary>
    private const string CacheKey = "SupporterUnlocked";

    private static StoreContext? _context;

    /// <summary>
    /// Whether the extras are unlocked. Builds outside the Store (the dev build) have no Store
    /// license to check, so they're unlocked; set WINDSHOT_SUPPORTER=0 to see the locked app.
    /// </summary>
    public static bool IsUnlocked { get; private set; } = !AppPackage.IsPackaged
        ? Environment.GetEnvironmentVariable("WINDSHOT_SUPPORTER") != "0"
        : ReadCache();

    /// <summary>Raised on the UI thread when <see cref="IsUnlocked"/> changes.</summary>
    public static event Action? Changed;

    /// <summary>The add-on's price as the Store shows it, e.g. "$4.99", once known.</summary>
    public static string? Price { get; private set; }

    private static StoreContext? Context => AppPackage.IsPackaged ? _context ??= StoreContext.GetDefault() : null;

    /// <summary>Checks the license; call on the UI thread at startup.</summary>
    public static async Task RefreshAsync()
    {
        if (Context is not { } context)
            return;
        try
        {
            var license = await context.GetAppLicenseAsync();
            // Add-on licenses are keyed by "<Store ID>/<SKU>".
            bool unlocked = license.AddOnLicenses.Any(pair =>
                pair.Key.StartsWith(AddOnStoreId, StringComparison.OrdinalIgnoreCase) && pair.Value.IsActive);
            Set(unlocked);
        }
        catch (Exception ex)
        {
            Log.Write($"Couldn't check the supporter license: {ex.Message}");
        }

        try
        {
            var products = await context.GetStoreProductsAsync(["Durable"], [AddOnStoreId]);
            if (products.Products.TryGetValue(AddOnStoreId, out var product))
                Price = product.Price.FormattedPrice;
        }
        catch (Exception ex)
        {
            Log.Write($"Couldn't get the supporter price: {ex.Message}");
        }
    }

    /// <summary>Shows the Store's purchase dialog over <paramref name="window"/>.</summary>
    /// <returns>A problem to show, or null when it's bought or the person changed their mind.</returns>
    public static async Task<string?> PurchaseAsync(Microsoft.UI.Xaml.Window window)
    {
        if (Context is not { } context)
            return "Supporting Windshot works in the Microsoft Store version.";
        try
        {
            // A desktop app has to tell the Store which window its dialog belongs to.
            WinRT.Interop.InitializeWithWindow.Initialize(context, WinRT.Interop.WindowNative.GetWindowHandle(window));
            var result = await context.RequestPurchaseAsync(AddOnStoreId);
            Log.Write($"Supporter purchase: {result.Status}");
            switch (result.Status)
            {
                case StorePurchaseStatus.Succeeded:
                case StorePurchaseStatus.AlreadyPurchased:
                    Set(true);
                    return null;
                case StorePurchaseStatus.NotPurchased:
                    return null; // closed the dialog
                case StorePurchaseStatus.NetworkError:
                    return "Microsoft Store couldn't be reached. Check your internet connection and try again.";
                default:
                    return "Microsoft Store couldn't complete the purchase. Try again later.";
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Supporter purchase failed: {ex.Message}");
            return "Microsoft Store couldn't complete the purchase. Try again later.";
        }
    }

    private static void Set(bool unlocked)
    {
        WriteCache(unlocked);
        if (unlocked == IsUnlocked)
            return;
        IsUnlocked = unlocked;
        Log.Write(unlocked ? "Supporter extras unlocked" : "Supporter extras locked");
        Changed?.Invoke();
    }

    private static bool ReadCache()
    {
        try
        {
            return Windows.Storage.ApplicationData.Current.LocalSettings.Values[CacheKey] is true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void WriteCache(bool unlocked)
    {
        try
        {
            Windows.Storage.ApplicationData.Current.LocalSettings.Values[CacheKey] = unlocked;
        }
        catch (Exception)
        {
        }
    }
}
