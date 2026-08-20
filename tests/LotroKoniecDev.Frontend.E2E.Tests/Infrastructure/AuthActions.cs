using Microsoft.Playwright;

namespace LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;

/// <summary>
/// The reusable steps of the account loop: register on the auth server's Razor Pages, confirm through
/// the link in Mailpit, and log in through the frontend's OIDC challenge, so the frontend ends up logged
/// in too and not only the auth server.
/// Registration happens on the auth origin at <c>/Account/Register</c> and not on the frontend, and the
/// pages exist in Polish only.
/// Elements are found by role or label, and <c>data-testid</c> is used only for the consent checkboxes
/// and the state panels.
/// </summary>
internal static class AuthActions
{
    // Matches part of the real subject, "Potwierdź konto - <brand>" (#541). It ignores the brand name on
    // purpose, so renaming it does not break this flow again.
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
        await page.GetByTestId(TestIds.RegisterAcceptDataProcessing).CheckViaLabelAsync();
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
            // The e-mail rides outbox -> broker -> consumer -> Mailpit; only the auth-api logs
            // say which leg failed, and the containers are gone by the time a human looks.
            throw new TimeoutException($"{ex.Message}\n{await fixture.GetAuthApiLogsAsync()}", ex);
        }

        await page.GotoAsync(link);
        await page.GetByTestId(TestIds.ConfirmEmailSuccess).WaitForAsync(LongWait);
    }

    public static async Task LoginAsync(IPage page, PlaywrightStackFixture fixture, TestUser user)
    {
        // Enter through the Frontend so the FE (not only the Auth server) ends up authenticated:
        // the home nav's "Zaloguj" link hits /auth/login → OIDC challenge → Auth login page →
        // callback → FE authenticated (the "Wyloguj" button appears).
        await page.GotoAsync($"{fixture.FrontendBaseUrl}/");
        await page.GetByRole(AriaRole.Link, new() { Name = Links.Login, Exact = true }).ClickAsync();

        await page.GetByLabel(FieldLabels.Email).FillAsync(user.Email);
        await page.GetByLabel(FieldLabels.Password, new() { Exact = true }).FillAsync(user.Password);
        await page.GetByRole(AriaRole.Button, new() { Name = Buttons.Login, Exact = true }).ClickAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = Buttons.Logout, Exact = true }).WaitForAsync(LongWait);
    }

    /// <summary>
    /// Accepts the LEGAL-04 cookie banner so its fixed bottom overlay never covers the controls a
    /// flow clicks lower on the page. Call once after the first Frontend page load of a fresh
    /// browser context.
    /// </summary>
    public static async Task AcceptCookieBannerAsync(IPage page)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "Akceptuję", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Region, new() { Name = "Informacja o plikach cookie" })
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached, Timeout = 30_000 });
    }

    public static async Task LogoutAsync(IPage page)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = Buttons.Logout, Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = Links.Login, Exact = true }).WaitForAsync(LongWait);
    }
}
