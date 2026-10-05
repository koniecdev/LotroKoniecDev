using LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Shouldly;

namespace LotroKoniecDev.Frontend.E2E.Tests.Flows;

/// <summary>
/// Regression guard for #941. After a confirmed address or a new password, Back loaded the form the one-time
/// link opened, with a live button. A second press sent the used link, and the page said it was dead,
/// although the change had worked. The page that did the work now leaves a marker cookie in this browser,
/// so the link's page shows the success answer again (ADR-0063).
/// Each flow goes Back from the success page and checks what the page shows: the success answer, no
/// button to press, no alert, and still only one POST. The last test covers a browser that brings the form
/// back from its cache instead of loading it again.
/// Nothing has to be seeded: each flow creates its own account.
/// </summary>
public sealed class OneTimeLinkBackTests : E2ETestBase
{
    /// <summary>
    /// A change request sends two e-mails. Only this one, sent to the new address, carries the confirm link.
    /// </summary>
    private const string ConfirmNewAddressSubject = "Potwierdź nowy adres e-mail";

    private const string PasswordResetSubject = "Reset hasła";
    private const string ConfirmEmailChangePath = "/Account/ConfirmEmailChange";
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
        TestUser user = TestUser.CreateRandom();
        await AuthActions.RegisterAsync(Page, Fixture, user);
        await AuthActions.ConfirmEmailAsync(Page, Fixture, user);
        await AuthActions.LoginAsync(Page, Fixture, user);
        await AuthActions.AcceptCookieBannerAsync(Page);
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
    /// A browser can bring the form back from its cache on Back, without asking the server, and then the
    /// marker cookie is never read. So a form that was already sent asks for a fresh GET of its own page.
    /// The test fires the event such a restore fires. The links only have to parse, and the test cancels
    /// the submit, so the server never sees a POST.
    /// </summary>
    [Theory]
    [InlineData("/Account/ConfirmEmailChange?userId=00000000-0000-0000-0000-000000000001&email=c%40d.pl&token=z")]
    [InlineData("/Account/ResetPassword?email=a%40b.pl&token=z")]
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
}
