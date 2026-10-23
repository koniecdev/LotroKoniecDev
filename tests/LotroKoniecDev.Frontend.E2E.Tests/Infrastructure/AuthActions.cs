using Microsoft.Playwright;

namespace LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;

/// <summary>
/// The reusable steps of the account loop. Registration lives on the auth origin, in Polish only. Elements
/// are found by role or label where the page gives them one, otherwise by <c>data-testid</c> or id.
/// </summary>
internal static class AuthActions
{
    // Part of "Potwierdź konto - <brand>", so a brand rename does not break this flow (#541).
    private const string ConfirmationSubject = "Potwierdź konto";
    private const string ConfirmationLinkPath = "/Account/ConfirmEmail";
    private static readonly TimeSpan MailTimeout = TimeSpan.FromSeconds(45);
    private static readonly LocatorWaitForOptions LongWait = new() { Timeout = 30_000 };

    public static async Task RegisterAsync(IPage page, PlaywrightStackFixture fixture, TestUser user)
    {
        await page.GotoAsync($"{fixture.AuthBaseUrl}/Account/Register");

        await page.GetByLabel(FieldLabels.Username).FillAsync(user.Username);
        await page.GetByLabel(FieldLabels.Email).FillAsync(user.Email);
        await page.GetByLabel(FieldLabels.Password, new() { Exact = true }).FillAsync(user.Password);
        await page.GetByLabel(FieldLabels.ConfirmPassword).FillAsync(user.Password);
        await page.GetByTestId(TestIds.RegisterAcceptPrivacy).CheckViaLabelAsync();
        await page.GetByTestId(TestIds.RegisterAcceptTerms).CheckViaLabelAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = Buttons.Register, Exact = true }).ClickAsync();

        await page.GetByTestId(TestIds.RegisterSuccess).WaitForAsync(LongWait);
    }

    public static async Task ConfirmEmailAsync(IPage page, PlaywrightStackFixture fixture, TestUser user)
    {
        string link;
        try
        {
            link = await MailpitClient.WaitForLinkAsync(
                fixture.MailpitBaseUrl, user.Email, ConfirmationSubject, ConfirmationLinkPath, MailTimeout);
        }
        catch (TimeoutException ex)
        {
            // Only the auth-api logs say which leg failed, and the containers are gone by the time a human
            // looks.
            throw new TimeoutException($"{ex.Message}\n{await fixture.GetAuthApiLogsAsync()}", ex);
        }

        await page.GotoAsync(link);
        await page.GetByTestId(TestIds.ConfirmEmailSuccess).WaitForAsync(LongWait);
    }

    public static async Task LoginAsync(IPage page, PlaywrightStackFixture fixture, TestUser user)
    {
        // Enter through the frontend, so it ends up signed in too, not only the auth server.
        await page.GotoAsync($"{fixture.FrontendBaseUrl}/");
        await page.GetByRole(AriaRole.Link, new() { Name = Links.Login, Exact = true }).ClickAsync();

        await page.GetByLabel(FieldLabels.Email).FillAsync(user.Email);
        await page.GetByLabel(FieldLabels.Password, new() { Exact = true }).FillAsync(user.Password);
        await page.GetByRole(AriaRole.Button, new() { Name = Buttons.Login, Exact = true }).ClickAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = Buttons.Logout, Exact = true }).WaitForAsync(LongWait);
    }

    /// <summary>
    /// So the banner never covers the controls a flow clicks. Call once after the first frontend page load
    /// of a fresh browser context.
    /// </summary>
    public static async Task AcceptCookieBannerAsync(IPage page)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "Akceptuję", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Region, new() { Name = "Informacja o plikach cookie" })
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached, Timeout = 30_000 });
    }

    /// <summary>The caller must be signed in with the cookie banner already accepted.</summary>
    public static async Task RequestEmailChangeAsync(IPage page, TestUser user, string newEmail)
    {
        await page.GetByTestId("nav-account").ClickAsync();
        await page.GetByTestId("account-change-email").ClickAsync();
        await page.Locator("#new-email").WaitForAsync(LongWait);
        await page.Locator("#new-email").FillAsync(newEmail);
        await page.Locator("#repeat-email").FillAsync(newEmail);
        await page.Locator("#current-password").FillAsync(user.Password);
        await page.GetByTestId("change-email-submit").ClickAsync();
    }

    /// <summary>
    /// Ends on the signed-out "deletion scheduled" page. The caller must be signed in with the cookie
    /// banner already accepted.
    /// </summary>
    public static async Task ScheduleDeletionAsync(IPage page, string password)
    {
        await page.GetByTestId("nav-account").ClickAsync();
        await page.GetByTestId("account-delete").ClickAsync();
        await page.Locator("#delete-password").WaitForAsync(LongWait);
        await page.Locator("#delete-password").FillAsync(password);
        await page.Locator("#delete-confirm").FillAsync("USUWAM");
        await page.GetByTestId("delete-submit").ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Przejdź dalej", Exact = true }).ClickAsync();
        await page.GetByTestId("deletion-date-line").WaitForAsync(LongWait);
    }

    public static async Task LogoutAsync(IPage page)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = Buttons.Logout, Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = Links.Login, Exact = true }).WaitForAsync(LongWait);
    }
}
