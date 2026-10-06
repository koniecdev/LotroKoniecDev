using LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Shouldly;

namespace LotroKoniecDev.Frontend.E2E.Tests.Flows;

/// <summary>
/// Regression guard for #941. After a confirmed address or a new password, Back loaded the form the one-time
/// link opened, with a live button. A second press sent the used link, and the page said it was dead,
/// although the change had worked. The undo and cancel links had the same gap one step later, on Back from
/// the password form they lead to. The page that did the work now leaves a cookie in this browser, so the
/// used link gives its first answer again (ADR-0063).
/// Each flow goes Back from its success page and checks what the page shows: the success answer, no alert,
/// and still only one POST. The last tests cover a browser that brings the form back from its cache instead
/// of loading it again.
/// Nothing has to be seeded: each flow creates its own account.
/// </summary>
public sealed class OneTimeLinkBackTests : E2ETestBase
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
    /// The undo link ends on the password form, so that form is its success page. Back from it loads the
    /// used undo link, which now sends this browser on to the same password form.
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

    /// <summary>
    /// The cancel link ends on the password form too, and Back from it works the same way.
    /// </summary>
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
    /// A browser can bring the form back from its cache on Back, without asking the server, and then the
    /// marker cookie is never read. So a form that was already sent asks for a fresh GET of its own page.
    /// The test fires the event such a restore fires. The links only have to parse, and the test cancels
    /// the submit, so the server never sees a POST.
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
    /// The login form carries no one-time link, so a fresh GET would show it again. It only gets its
    /// button back (#871). The listener runs inside dispatchEvent, so the button state is final when the
    /// call returns.
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
