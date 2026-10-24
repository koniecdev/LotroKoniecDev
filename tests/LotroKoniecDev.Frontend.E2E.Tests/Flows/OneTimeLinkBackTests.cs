using LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Shouldly;

namespace LotroKoniecDev.Frontend.E2E.Tests.Flows;

/// <summary>
/// #941: Back from a one-time link's success page must give the link's first answer again, not a live form
/// that reports the used link as dead (ADR-0063). Each flow checks the success answer, no alert and one
/// POST. The last tests cover a form the browser brings back from its cache. Each flow creates its own
/// account.
/// </summary>
public sealed class OneTimeLinkBackTests : E2ETestBase
{
    /// <summary>
    /// A change request sends two e-mails. Only this one, sent to the new address, carries the confirm link.
    /// </summary>
    private const string ConfirmNewAddressSubject = "Potwierdź nowy adres e-mail";

    /// <summary>
    /// The new address gets a notice with a similar subject but no undo link; the search is by the old
    /// address, so only this one matches.
    /// </summary>
    private const string RevertOfferSubject = "Adres e-mail Twojego konta został zmieniony";

    private const string DeletionScheduledSubject = "Zaplanowano usunięcie konta";
    private const string PasswordResetSubject = "Reset hasła";
    private const string ConfirmEmailChangePath = "/Account/ConfirmEmailChange";
    private const string RevertEmailChangePath = "/Account/RevertEmailChange";
    private const string CancelDeletionPath = "/Account/CancelDeletion";
    private const string ResetPasswordPath = "/Account/ResetPassword";

    private static readonly LocatorWaitForOptions LongWait = new() { Timeout = 30_000 };
    private static readonly TimeSpan MailTimeout = TimeSpan.FromSeconds(45);

    public OneTimeLinkBackTests(PlaywrightStackFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Back_after_confirming_the_new_address_shows_the_change_as_done_instead_of_the_form()
    {
        // Arrange
        TestUser user = await CreateSignedInUserAsync();
        string newEmail = TestUser.CreateRandomEmail();
        await AuthActions.RequestEmailChangeAsync(Page, user, newEmail);
        string confirmLink = await MailpitClient.WaitForLinkAsync(
            Fixture.MailpitBaseUrl, newEmail, ConfirmNewAddressSubject, ConfirmEmailChangePath, MailTimeout);
        await Page.GotoAsync(confirmLink);
        PostWatch posts = PostWatch.StartCounting(Page);
        ILocator success = Page.GetByTestId("confirm-email-change-success");
        await Page.GetByTestId("confirm-email-change-submit").ClickAsync();
        await success.Or(Page.GetByRole(AriaRole.Alert)).First.WaitForAsync(LongWait);

        // Act
        await Page.GoBackAsync();
        await success.Or(Page.GetByRole(AriaRole.Alert)).First.WaitForAsync(LongWait);

        // Assert
        new Uri(Page.Url).PathAndQuery.ShouldBe(new Uri(confirmLink).PathAndQuery);
        (await Page.GetByRole(AriaRole.Alert).CountAsync()).ShouldBe(0);
        (await success.CountAsync()).ShouldBe(1);
        (await Page.GetByTestId("confirm-email-change-submit").CountAsync()).ShouldBe(0);
        posts.PostsTo(ConfirmEmailChangePath).ShouldBe(1);
        CspViolations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Back_after_setting_a_new_password_shows_the_password_as_changed_instead_of_the_form()
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
        string newPassword = TestUser.ComposePassword("Back");
        await Page.GetByRole(AriaRole.Textbox, new() { Name = "Nowe hasło", Exact = true }).FillAsync(newPassword);
        await Page.GetByRole(AriaRole.Textbox, new() { Name = "Powtórz nowe hasło", Exact = true }).FillAsync(newPassword);
        PostWatch posts = PostWatch.StartCounting(Page);
        ILocator success = Page.GetByTestId("reset-password-success");
        await Page.GetByTestId("reset-password-submit").ClickAsync();
        await success.Or(Page.GetByRole(AriaRole.Alert)).First.WaitForAsync(LongWait);

        // Act
        await Page.GoBackAsync();
        await success.Or(Page.GetByRole(AriaRole.Alert)).First.WaitForAsync(LongWait);

        // Assert
        new Uri(Page.Url).PathAndQuery.ShouldBe(new Uri(resetLink).PathAndQuery);
        (await Page.GetByRole(AriaRole.Alert).CountAsync()).ShouldBe(0);
        (await success.CountAsync()).ShouldBe(1);
        (await Page.GetByTestId("reset-password-submit").CountAsync()).ShouldBe(0);
        posts.PostsTo(ResetPasswordPath).ShouldBe(1);
        CspViolations.ShouldBeEmpty();
    }

    /// <summary>
    /// The undo link ends on the password form, so that form is its success page.
    /// </summary>
    [Fact]
    public async Task Back_from_the_password_form_after_undoing_an_email_change_opens_the_same_password_form()
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
        PostWatch posts = PostWatch.StartCounting(Page);
        ILocator passwordForm = Page.GetByTestId("reset-password-submit");
        await Page.GetByTestId("revert-email-change-submit").ClickAsync();
        await passwordForm.Or(Page.GetByRole(AriaRole.Alert)).First.WaitForAsync(LongWait);
        string passwordFormUrl = Page.Url;

        // Act
        await Page.GoBackAsync();
        await passwordForm.Or(Page.GetByRole(AriaRole.Alert)).First.WaitForAsync(LongWait);

        // Assert
        new Uri(Page.Url).PathAndQuery.ShouldBe(new Uri(passwordFormUrl).PathAndQuery);
        (await Page.GetByRole(AriaRole.Alert).CountAsync()).ShouldBe(0);
        (await passwordForm.CountAsync()).ShouldBe(1);
        posts.PostsTo(RevertEmailChangePath).ShouldBe(1);
        CspViolations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Back_from_the_password_form_after_cancelling_a_deletion_opens_the_same_password_form()
    {
        // Arrange
        TestUser user = await CreateSignedInUserAsync();
        await AuthActions.ScheduleDeletionAsync(Page, user.Password);
        string cancelLink = await MailpitClient.WaitForLinkAsync(
            Fixture.MailpitBaseUrl, user.Email, DeletionScheduledSubject, CancelDeletionPath, MailTimeout);
        await Page.GotoAsync(cancelLink);
        PostWatch posts = PostWatch.StartCounting(Page);
        ILocator passwordForm = Page.GetByTestId("reset-password-submit");
        await Page.GetByTestId("cancel-deletion-submit").ClickAsync();
        await passwordForm.Or(Page.GetByRole(AriaRole.Alert)).First.WaitForAsync(LongWait);
        string passwordFormUrl = Page.Url;

        // Act
        await Page.GoBackAsync();
        await passwordForm.Or(Page.GetByRole(AriaRole.Alert)).First.WaitForAsync(LongWait);

        // Assert
        new Uri(Page.Url).PathAndQuery.ShouldBe(new Uri(passwordFormUrl).PathAndQuery);
        (await Page.GetByRole(AriaRole.Alert).CountAsync()).ShouldBe(0);
        (await passwordForm.CountAsync()).ShouldBe(1);
        posts.PostsTo(CancelDeletionPath).ShouldBe(1);
        CspViolations.ShouldBeEmpty();
    }

    /// <summary>
    /// A form restored from the cache never reads the marker cookie, so a sent form asks for a fresh GET.
    /// The test fires the restore event and cancels the submit, so the links only have to parse.
    /// </summary>
    [Theory]
    [InlineData("/Account/ConfirmEmailChange?userId=00000000-0000-0000-0000-000000000001&email=c%40d.pl&token=z")]
    [InlineData("/Account/ResetPassword?email=a%40b.pl&token=z")]
    [InlineData("/Account/RevertEmailChange?userId=00000000-0000-0000-0000-000000000001&from=a%40b.pl&to=c%40d.pl&token=z")]
    [InlineData("/Account/CancelDeletion?email=a%40b.pl&token=z")]
    public async Task A_sent_one_time_link_form_brought_back_from_the_cache_loads_its_page_again(string path)
    {
        // Arrange
        await Page.GotoAsync(Fixture.AuthBaseUrl + path);
        foreach (ILocator input in await Page.Locator("form[data-submit-once] input[required]").AllAsync())
        {
            await input.FillAsync(TestUser.ComposePassword("Cache"));
        }

        await Page.Locator("form[data-submit-once]").EvaluateAsync(
            "form => form.addEventListener('submit', event => event.preventDefault())");
        await Page.Locator("form[data-submit-once] button[type=submit]").ClickAsync();

        // Act
        IRequest reload = await Page.RunAndWaitForRequestAsync(
            () => Page.EvaluateAsync("() => window.dispatchEvent(new PageTransitionEvent('pageshow', { persisted: true }))"),
            request => request.IsNavigationRequest);

        // Assert
        reload.Method.ShouldBe("GET");
        new Uri(reload.Url).PathAndQuery.ShouldBe(new Uri(Fixture.AuthBaseUrl + path).PathAndQuery);
        CspViolations.ShouldBeEmpty();
    }

    /// <summary>
    /// The login form carries no one-time link, so it only gets its button back (#871). The listener runs
    /// inside dispatchEvent, so the button state is final when the call returns.
    /// </summary>
    [Fact]
    public async Task A_sent_form_without_the_mark_brought_back_from_the_cache_only_gets_its_button_back()
    {
        // Arrange
        const string path = "/Account/Login";
        await Page.GotoAsync(Fixture.AuthBaseUrl + path);
        await Page.GetByLabel(FieldLabels.Email).FillAsync(TestUser.CreateRandomEmail());
        await Page.GetByLabel(FieldLabels.Password, new() { Exact = true }).FillAsync(TestUser.ComposePassword("Cache"));
        await Page.Locator("form[data-submit-once]").EvaluateAsync(
            "form => form.addEventListener('submit', event => event.preventDefault())");
        ILocator button = Page.Locator("form[data-submit-once] button[type=submit]");
        await button.ClickAsync();
        (await button.IsDisabledAsync()).ShouldBeTrue();

        // Act
        await Page.EvaluateAsync("() => window.dispatchEvent(new PageTransitionEvent('pageshow', { persisted: true }))");

        // Assert
        (await button.IsDisabledAsync()).ShouldBeFalse();
        new Uri(Page.Url).PathAndQuery.ShouldBe(new Uri(Fixture.AuthBaseUrl + path).PathAndQuery);
        CspViolations.ShouldBeEmpty();
    }

    private async Task<TestUser> CreateSignedInUserAsync()
    {
        TestUser user = TestUser.CreateRandom();
        await AuthActions.RegisterAsync(Page, Fixture, user);
        await AuthActions.ConfirmEmailAsync(Page, Fixture, user);
        await AuthActions.LoginAsync(Page, Fixture, user);
        await AuthActions.AcceptCookieBannerAsync(Page);
        return user;
    }
}
