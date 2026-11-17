using Microsoft.Playwright;

namespace LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;

internal static class PlaywrightExtensions
{
    /// <summary>
    /// The auth pages hide the real checkbox under a styled <c>.box</c> span, so Playwright cannot click the
    /// input itself. Like a user, the helper clicks the box, and the wrapping label ticks the input.
    /// Idempotent.
    /// </summary>
    public static async Task CheckViaLabelAsync(this ILocator checkbox)
    {
        if (await checkbox.IsCheckedAsync())
        {
            return;
        }

        // Not the middle of the label: the privacy-consent label holds a link, and on a wide viewport its
        // middle lands on it, which follows the link instead of ticking the box.
        await checkbox.Locator("xpath=ancestor::label[1]").Locator("span.box").ClickAsync();
    }

    public static async Task<LocatorBoundingBoxResult> RequireBoundingBoxAsync(this ILocator locator) =>
        await locator.BoundingBoxAsync()
        ?? throw new InvalidOperationException($"'{locator}' has no bounding box — it is not rendered.");

    /// <summary>
    /// <c>font-display: swap</c> means a late font would move the page after it was measured.
    /// </summary>
    public static async Task WaitForFontsAsync(this IPage page) =>
        await page.EvaluateAsync<bool>("document.fonts.ready.then(() => true)");
}
