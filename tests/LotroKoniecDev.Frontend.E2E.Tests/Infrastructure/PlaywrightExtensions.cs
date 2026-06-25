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

        // Click the box, not the label's geometric center: the privacy-consent label's text wraps an
        // <a> (the privacy-policy link), and on a wide single-line viewport the label center lands on
        // that link — per the HTML spec a click on interactive content inside a <label> activates the
        // link instead of toggling the control. The .box span is inert, so clicking it always toggles.
        await checkbox.Locator("xpath=ancestor::label[1]").Locator("span.box").ClickAsync();
    }
}
