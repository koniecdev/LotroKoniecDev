using Microsoft.Playwright;

namespace LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;

/// <summary>
/// The auth pages hide the real checkbox under a styled <c>.box</c> span, so Playwright cannot click the
/// input itself. Like a user, the helper clicks the box, and the wrapping label ticks the input.
/// </summary>
internal static class PlaywrightExtensions
{
    /// <summary>Idempotent.</summary>
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
}
