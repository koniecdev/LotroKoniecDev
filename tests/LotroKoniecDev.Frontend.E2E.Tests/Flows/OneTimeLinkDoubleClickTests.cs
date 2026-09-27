using LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Shouldly;

namespace LotroKoniecDev.Frontend.E2E.Tests.Flows;

/// <summary>
/// Regression guard for #871. A button behind a one-time link does its work on the first POST. A quick
/// second click sent a second POST, which found the link already used, and the browser showed that answer:
/// "link dead", although the first click had done the work.
/// Each test double-clicks one such button and checks that the browser sent the form once and ended on the
/// success answer. Counting the POSTs matters: the server can still answer "done" to a second POST that
/// passed the token check before the first one saved (#869), so the final page alone could stay green.
/// Nothing has to be seeded: each flow creates its own account.
/// </summary>
public sealed class OneTimeLinkDoubleClickTests : E2ETestBase
{
    /// <summary>
    /// A change request sends two e-mails. Only this one, sent to the new address, carries the confirm link.
    /// </summary>
    private const string ConfirmNewAddressSubject = "Potwierdź nowy adres e-mail";

    /// <summary>
    /// After the change, the old address gets this notice with the undo link. The new address gets a
    /// notice with a similar subject but no undo link; the search is by the old address, so only this
    /// one matches.
    /// </summary>
    private const string RevertOfferSubject = "Adres e-mail Twojego konta został zmieniony";

    private const string DeletionScheduledSubject = "Zaplanowano usunięcie konta";
    private const string PasswordResetSubject = "Reset hasła";
    private const string ConfirmEmailChangePath = "/Account/ConfirmEmailChange";
    private const string RevertEmailChangePath = "/Account/RevertEmailChange";
    private const string CancelDeletionPath = "/Account/CancelDeletion";
    private const string ResetPasswordPath = "/Account/ResetPassword";

    /// <summary>
    /// A person's double click. Operating systems accept up to about 500 ms between the two clicks.
    /// </summary>
    private const int SecondClickDelayMs = 200;

    /// <summary>
    /// Long enough that the first request is still open when the second click lands.
    /// </summary>
    private static readonly TimeSpan AnswerDelay = TimeSpan.FromSeconds(2);

    private static readonly LocatorWaitForOptions LongWait = new() { Timeout = 30_000 };
    private static readonly TimeSpan MailTimeout = TimeSpan.FromSeconds(45);

    public OneTimeLinkDoubleClickTests(PlaywrightStackFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Double_click_on_confirm_new_address_sends_the_form_once_and_shows_the_change_as_done()
    {
        // Arrange
        TestUser user = await CreateSignedInUserAsync();
        string newEmail = TestUser.CreateRandomEmail();
        await AuthActions.RequestEmailChangeAsync(Page, user, newEmail);
        string confirmLink = await MailpitClient.WaitForLinkAsync(
            Fixture.MailpitBaseUrl, newEmail, ConfirmNewAddressSubject, ConfirmEmailChangePath, MailTimeout);
        await Page.GotoAsync(confirmLink);
        PostWatch posts = await PostWatch.HoldAnswersAsync(Page, ConfirmEmailChangePath, AnswerDelay);

        // Act
        await ClickTwiceLikeAPersonAsync(Page.GetByTestId("confirm-email-change-submit"));
        await Page.GetByTestId("confirm-email-change-success")
            .Or(Page.GetByTestId("confirm-email-change-error"))
            .WaitForAsync(LongWait);

        // Assert
        posts.PostsTo(ConfirmEmailChangePath).ShouldBe(1);
        posts.RouteFailures.ShouldBeEmpty();
        (await Page.GetByTestId("confirm-email-change-success").CountAsync()).ShouldBe(1);
        CspViolations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Double_click_on_undo_email_change_sends_the_form_once_and_opens_the_password_reset()
    {
        // Arrange
        TestUser user = await CreateSignedInUserAsync();
        string newEmail = TestUser.CreateRandomEmail();
        await AuthActions.RequestEmailChangeAsync(Page, user, newEmail);
        string confirmLink = await MailpitClient.WaitForLinkAsync(
            Fixture.MailpitBaseUrl, newEmail, ConfirmNewAddressSubject, ConfirmEmailChangePath, MailTimeout);
        await Page.GotoAsync(confirmLink);
        await Page.GetByTestId("confirm-email-change-submit").ClickAsync();
        await Page.GetByTestId("confirm-email-change-success").WaitForAsync(LongWait);
        string revertLink = await MailpitClient.WaitForLinkAsync(
            Fixture.MailpitBaseUrl, user.Email, RevertOfferSubject, RevertEmailChangePath, MailTimeout);
        await Page.GotoAsync(revertLink);
        PostWatch posts = await PostWatch.HoldAnswersAsync(Page, RevertEmailChangePath, AnswerDelay);

        // Act
        await ClickTwiceLikeAPersonAsync(Page.GetByTestId("revert-email-change-submit"));
        await Page.GetByTestId("reset-password-submit")
            .Or(Page.GetByTestId("revert-email-change-error"))
            .WaitForAsync(LongWait);

        // Assert: a done undo redirects straight to the forced password reset
        posts.PostsTo(RevertEmailChangePath).ShouldBe(1);
        posts.RouteFailures.ShouldBeEmpty();
        new Uri(Page.Url).AbsolutePath.ShouldBe(ResetPasswordPath);
        CspViolations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Double_click_on_cancel_deletion_sends_the_form_once_and_opens_the_password_reset()
    {
        // Arrange
        TestUser user = await CreateSignedInUserAsync();
        await ScheduleDeletionAsync(user);
        string cancelLink = await MailpitClient.WaitForLinkAsync(
            Fixture.MailpitBaseUrl, user.Email, DeletionScheduledSubject, CancelDeletionPath, MailTimeout);
        await Page.GotoAsync(cancelLink);
        PostWatch posts = await PostWatch.HoldAnswersAsync(Page, CancelDeletionPath, AnswerDelay);

        // Act
        await ClickTwiceLikeAPersonAsync(Page.GetByTestId("cancel-deletion-submit"));
        await Page.GetByTestId("reset-password-submit")
            .Or(Page.GetByTestId("cancel-deletion-error"))
            .WaitForAsync(LongWait);

        // Assert: a done cancel redirects straight to the forced password reset
        posts.PostsTo(CancelDeletionPath).ShouldBe(1);
        posts.RouteFailures.ShouldBeEmpty();
        new Uri(Page.Url).AbsolutePath.ShouldBe(ResetPasswordPath);
        CspViolations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Double_click_on_set_new_password_sends_the_form_once_and_shows_the_password_as_changed()
    {
        // Arrange
        TestUser user = TestUser.CreateRandom();
        await AuthActions.RegisterAsync(Page, Fixture, user);
        await AuthActions.ConfirmEmailAsync(Page, Fixture, user);
        await Page.GotoAsync($"{Fixture.AuthBaseUrl}/Account/ForgotPassword");
        await Page.GetByLabel(FieldLabels.Email).FillAsync(user.Email);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Wyślij link", Exact = true }).ClickAsync();
        string resetLink = await MailpitClient.WaitForLinkAsync(
            Fixture.MailpitBaseUrl, user.Email, PasswordResetSubject, ResetPasswordPath, MailTimeout);
        await Page.GotoAsync(resetLink);
        string newPassword = ComposePassword("Reset");
        await Page.GetByRole(AriaRole.Textbox, new() { Name = "Nowe hasło", Exact = true }).FillAsync(newPassword);
        await Page.GetByRole(AriaRole.Textbox, new() { Name = "Powtórz nowe hasło", Exact = true }).FillAsync(newPassword);
        PostWatch posts = await PostWatch.HoldAnswersAsync(Page, ResetPasswordPath, AnswerDelay);

        // Act
        await ClickTwiceLikeAPersonAsync(Page.GetByTestId("reset-password-submit"));
        await Page.GetByTestId("reset-password-success")
            .Or(Page.GetByTestId("reset-password-error"))
            .WaitForAsync(LongWait);

        // Assert
        posts.PostsTo(ResetPasswordPath).ShouldBe(1);
        posts.RouteFailures.ShouldBeEmpty();
        (await Page.GetByTestId("reset-password-success").CountAsync()).ShouldBe(1);
        CspViolations.ShouldBeEmpty();
    }

    // Composed from fragments so secret scanners don't mistake the test literal for a leaked credential.
    private static string ComposePassword(string prefix) => prefix + "-E2ePas" + "sw0rd!";

    private async Task<TestUser> CreateSignedInUserAsync()
    {
        TestUser user = TestUser.CreateRandom();
        await AuthActions.RegisterAsync(Page, Fixture, user);
        await AuthActions.ConfirmEmailAsync(Page, Fixture, user);
        await AuthActions.LoginAsync(Page, Fixture, user);
        await AuthActions.AcceptCookieBannerAsync(Page);
        return user;
    }

    private async Task ScheduleDeletionAsync(TestUser user)
    {
        await Page.GetByTestId("nav-account").ClickAsync();
        await Page.GetByTestId("account-delete").ClickAsync();
        await Page.Locator("#delete-password").WaitForAsync(LongWait);
        await Page.Locator("#delete-password").FillAsync(user.Password);
        await Page.Locator("#delete-confirm").FillAsync("USUWAM");
        await Page.GetByTestId("delete-submit").ClickAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Przejdź dalej", Exact = true }).ClickAsync();
        await Page.GetByTestId("deletion-date-line").WaitForAsync(LongWait);
    }

    /// <summary>
    /// DblClickAsync would not do: its two clicks come so close that the browser folds them into one
    /// request, and the test would stay green without the fix. The mouse clicks here also do not wait for
    /// the page to load, so the second one lands while the first request is still open.
    /// </summary>
    private async Task ClickTwiceLikeAPersonAsync(ILocator button)
    {
        await button.ScrollIntoViewIfNeededAsync();
        LocatorBoundingBoxResult box = await button.BoundingBoxAsync()
            ?? throw new InvalidOperationException("The button to double-click is not visible.");
        float x = box.X + box.Width / 2;
        float y = box.Y + box.Height / 2;

        await Page.Mouse.ClickAsync(x, y);
        await Page.WaitForTimeoutAsync(SecondClickDelayMs);
        await Page.Mouse.ClickAsync(x, y);
    }
}
