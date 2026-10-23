using LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Shouldly;

namespace LotroKoniecDev.Frontend.E2E.Tests.Flows;

/// <summary>
/// LEGAL-02: the self-service loop the privacy policy promises under "Moje konto": export, password change,
/// scheduled deletion, cancel through the e-mailed link, forced password reset and sign-in again. The flow
/// creates its own account.
/// </summary>
public sealed class AccountGdprSelfServiceTests : E2ETestBase
{
    private const string DeletionScheduledSubject = "Zaplanowano usunięcie konta";
    private const string CancelDeletionLinkPath = "/Account/CancelDeletion";
    private static readonly string ChangedPassword = TestUser.ComposePassword("Ch4nged");
    private static readonly string NewPassword = TestUser.ComposePassword("N3w");
    private static readonly TimeSpan MailTimeout = TimeSpan.FromSeconds(45);
    private static readonly LocatorWaitForOptions LongWait = new() { Timeout = 30_000 };

    public AccountGdprSelfServiceTests(PlaywrightStackFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Export_then_delete_then_cancel_via_email_then_reset_password_and_login_again()
    {
        // Arrange: a fresh confirmed account, signed in through the FE.
        TestUser user = TestUser.CreateRandom();
        await AuthActions.RegisterAsync(Page, Fixture, user);
        await AuthActions.ConfirmEmailAsync(Page, Fixture, user);
        await AuthActions.LoginAsync(Page, Fixture, user);

        // Accept the cookie banner first, or its bar would cover the form buttons this flow clicks.
        await AuthActions.AcceptCookieBannerAsync(Page);

        // "Moje konto" is the privacy policy's wording.
        await Page.GetByTestId("nav-account").ClickAsync();
        await Page.GetByTestId("account-export").WaitForAsync(LongWait);
        (await Page.GetByText(user.Email).First.IsVisibleAsync()).ShouldBeTrue();

        // The export asks for the current password first (ADR-0052). A wrong one hands over no file and
        // shows no form.
        await Page.GetByTestId("account-export").ClickAsync();
        await Page.Locator("#export-password").WaitForAsync(LongWait);
        await Page.Locator("#export-password").FillAsync("Wr0ng-Current!");
        await Page.GetByTestId("export-submit").ClickAsync();
        await Page.GetByTestId("export-error").WaitForAsync(LongWait);
        (await Page.GetByTestId("export-form").CountAsync()).ShouldBe(0);

        await Page.GetByTestId("export-retry").ClickAsync();
        await Page.Locator("#export-password").WaitForAsync(LongWait);
        (await Page.GetByTestId("export-error").CountAsync()).ShouldBe(0);

        await Page.Locator("#export-password").FillAsync(user.Password);
        IDownload download = await Page.RunAndWaitForDownloadAsync(
            () => Page.GetByTestId("export-submit").ClickAsync());
        download.SuggestedFilename.ShouldStartWith("lotro-translator-moje-dane-");
        download.SuggestedFilename.ShouldEndWith(".json");

        // The file came as an attachment, so the page never moved.
        (await Page.GetByTestId("export-error").CountAsync()).ShouldBe(0);

        // Without the antiforgery token the POST is refused. The requirement comes from binding a form field,
        // which is easy to undo by accident.
        IAPIResponse forged = await Context.APIRequest.PostAsync(
            new Uri(new Uri(Page.Url), "/account/export/download").ToString(),
            new APIRequestContextOptions
            {
                Form = Context.APIRequest.CreateFormData().Set("password", user.Password),
                MaxRedirects = 0
            });
        forged.Status.ShouldBe(400);

        await Page.GetByTestId("nav-account").ClickAsync();
        await Page.GetByTestId("account-change-password").WaitForAsync(LongWait);

        // A wrong current password shows the API error; the correct one changes the password.
        await Page.GetByTestId("account-change-password").ClickAsync();
        await Page.Locator("#current-password").WaitForAsync(LongWait);
        await Page.Locator("#current-password").FillAsync("Wr0ng-Current!");
        await Page.Locator("#new-password").FillAsync(ChangedPassword);
        await Page.Locator("#repeat-password").FillAsync(ChangedPassword);
        await Page.GetByTestId("change-password-submit").ClickAsync();
        await Page.Locator(".error-message").WaitForAsync(LongWait);

        await Page.Locator("#current-password").FillAsync(user.Password);
        await Page.Locator("#new-password").FillAsync(ChangedPassword);
        await Page.Locator("#repeat-password").FillAsync(ChangedPassword);
        await Page.GetByTestId("change-password-submit").ClickAsync();
        await Page.GetByText("Hasło zostało zmienione.").WaitForAsync(LongWait);

        // A fresh sign-in proves the changed password.
        await AuthActions.LogoutAsync(Page);
        await Page.GetByRole(AriaRole.Link, new() { Name = Links.Login, Exact = true }).ClickAsync();
        await Page.GetByLabel(FieldLabels.Email).FillAsync(user.Email);
        await Page.GetByLabel(FieldLabels.Password, new() { Exact = true }).FillAsync(ChangedPassword);
        await Page.GetByRole(AriaRole.Button, new() { Name = Buttons.Login, Exact = true }).ClickAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = Buttons.Logout, Exact = true }).WaitForAsync(LongWait);
        await Page.GetByTestId("nav-account").ClickAsync();
        await Page.GetByTestId("account-delete").WaitForAsync(LongWait);

        // A wrong confirmation phrase schedules nothing.
        await Page.GetByTestId("account-delete").ClickAsync();
        await Page.Locator("#delete-password").WaitForAsync(LongWait);
        await Page.Locator("#delete-password").FillAsync(ChangedPassword);
        await Page.Locator("#delete-confirm").FillAsync("USUN");
        await Page.GetByTestId("delete-submit").ClickAsync();
        await Page.GetByText("Nieprawidłowe potwierdzenie").WaitForAsync(LongWait);

        // The correct phrase schedules the deletion and ends on the confirmation page with the real date
        // from the X-Deletion-Finalizes-At header, not the generic fallback.
        await Page.Locator("#delete-password").FillAsync(ChangedPassword);
        await Page.Locator("#delete-confirm").FillAsync("USUWAM");
        await Page.GetByTestId("delete-submit").ClickAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Przejdź dalej", Exact = true }).ClickAsync();

        await Page.GetByTestId("deletion-date-line").WaitForAsync(LongWait);
        string dateLine = await Page.GetByTestId("deletion-date-line").InnerTextAsync();
        dateLine.ShouldContain("(czasu polskiego)");
        (await Page.GetByRole(AriaRole.Link, new() { Name = Links.Login, Exact = true }).IsVisibleAsync())
            .ShouldBeTrue();

        // The account is locked for the grace period, so even the correct password fails.
        await Page.GetByRole(AriaRole.Link, new() { Name = Links.Login, Exact = true }).ClickAsync();
        await Page.GetByLabel(FieldLabels.Email).FillAsync(user.Email);
        await Page.GetByLabel(FieldLabels.Password, new() { Exact = true }).FillAsync(ChangedPassword);
        await Page.GetByRole(AriaRole.Button, new() { Name = Buttons.Login, Exact = true }).ClickAsync();
        await Page.GetByRole(AriaRole.Alert).WaitForAsync(LongWait);

        // The cancel page lives on the auth server: GET renders, POST cancels.
        string cancelLink = await MailpitClient.WaitForLinkAsync(
            Fixture.MailpitBaseUrl, user.Email, DeletionScheduledSubject, CancelDeletionLinkPath, MailTimeout);
        await Page.GotoAsync(cancelLink);
        await Page.GetByTestId("cancel-deletion-submit").ClickAsync();

        // Cancelling invalidates the old password. Not GetByLabel: the reset panel's <section> has the
        // same aria label.
        await Page.GetByRole(AriaRole.Textbox, new() { Name = "Nowe hasło", Exact = true }).FillAsync(NewPassword);
        await Page.GetByRole(AriaRole.Textbox, new() { Name = "Powtórz nowe hasło", Exact = true }).FillAsync(NewPassword);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Ustaw nowe hasło", Exact = true }).ClickAsync();
        await Page.GetByRole(AriaRole.Status).WaitForAsync(LongWait);

        await Page.GotoAsync($"{Fixture.FrontendBaseUrl}/");
        await Page.GetByRole(AriaRole.Link, new() { Name = Links.Login, Exact = true }).ClickAsync();
        await Page.GetByLabel(FieldLabels.Email).FillAsync(user.Email);
        await Page.GetByLabel(FieldLabels.Password, new() { Exact = true }).FillAsync(NewPassword);
        await Page.GetByRole(AriaRole.Button, new() { Name = Buttons.Login, Exact = true }).ClickAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = Buttons.Logout, Exact = true }).WaitForAsync(LongWait);

        // The cancel link's form state and the forced reset ran under the auth CSP without a block (#693).
        CspViolations.ShouldBeEmpty();
    }

}
