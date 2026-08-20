using Microsoft.Playwright;

namespace LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;

/// <summary>
/// Playwright helpers for the Auth pages' custom-styled checkboxes. The real
/// <c>&lt;input type="checkbox"&gt;</c> is hidden with <c>opacity:0; pointer-events:none</c> and a styled
/// <c>.box</c> span is painted on top, so Playwright cannot click the input itself (its actionability
/// check never sees the input receive a pointer event). A real user toggles it by clicking that painted
/// box; this helper does the same, and the click propagates to the hidden input through the wrapping
/// <c>&lt;label&gt;</c>.
/// </summary>
internal static class PlaywrightExtensions
{
    /// <summary>
    /// Ensures a custom-styled checkbox ends up checked by clicking the decorative <c>.box</c> span
    /// inside its wrapping <c>&lt;label&gt;</c>, which natively toggles the hidden input. Idempotent.
    /// </summary>
    public static async Task CheckViaLabelAsync(this ILocator checkbox)
    {
        if (await checkbox.IsCheckedAsync())
        {
            return;
        }

        // Click the box and not the middle of the label. The privacy-consent label contains an <a>, the
        // privacy-policy link, and on a wide viewport where the label fits on one line its middle lands
        // on that link. By the HTML spec, clicking something interactive inside a <label> follows the
        // link instead of ticking the box. The .box span does nothing on its own, so clicking it always
        // ticks.
        await checkbox.Locator("xpath=ancestor::label[1]").Locator("span.box").ClickAsync();
    }
}
