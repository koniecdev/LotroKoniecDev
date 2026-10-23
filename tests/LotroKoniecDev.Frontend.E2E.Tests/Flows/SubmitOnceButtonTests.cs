using LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Shouldly;

namespace LotroKoniecDev.Frontend.E2E.Tests.Flows;

/// <summary>
/// #871: after the first click the button is disabled with its busy label and spinner. Every page has its
/// own copy of the spinner style, so each is checked. The test cancels the navigation, so the links only
/// have to parse.
/// </summary>
public sealed class SubmitOnceButtonTests : E2ETestBase
{
    private const string SubmitButton = "form[data-submit-once] button[type=submit]";
    private const string LoginPath = "/Account/Login";

    private static readonly LocatorWaitForOptions LongWait = new() { Timeout = 30_000 };

    public SubmitOnceButtonTests(PlaywrightStackFixture fixture) : base(fixture)
    {
    }

    [Theory]
    [InlineData(LoginPath)]
    [InlineData("/Account/ConfirmEmailChange?userId=00000000-0000-0000-0000-000000000001&email=c%40d.pl&token=z")]
    [InlineData("/Account/RevertEmailChange?userId=00000000-0000-0000-0000-000000000001&from=a%40b.pl&to=c%40d.pl&token=z")]
    [InlineData("/Account/CancelDeletion?email=a%40b.pl&token=z")]
    [InlineData("/Account/ResetPassword?email=a%40b.pl&token=z")]
    public async Task First_click_disables_the_button_and_shows_the_busy_label_and_the_spinner(string path)
    {
        // Arrange
        await Page.GotoAsync(Fixture.AuthBaseUrl + path);
        foreach (ILocator input in await Page.Locator("form[data-submit-once] input[required]").AllAsync())
        {
            string? type = await input.GetAttributeAsync("type");
            await input.FillAsync(type is "email" ? TestUser.CreateRandomEmail() : TestUser.ComposePassword("Busy"));
        }

        ILocator button = Page.Locator(SubmitButton);
        string busyLabel = await button.GetAttributeAsync("data-busy-label")
            ?? throw new InvalidOperationException($"The submit button on {path} has no busy label.");

        // The test cancels the navigation, after the page script, which still sees a normal submit.
        // Holding the request open made reading the page hang.
        await Page.Locator("form[data-submit-once]").EvaluateAsync(
            "form => form.addEventListener('submit', event => event.preventDefault())");

        // Act
        await button.ClickAsync();

        // Assert
        (await button.IsDisabledAsync()).ShouldBeTrue();
        (await button.Locator(".submit-btn-label").InnerTextAsync()).ShouldBe(busyLabel);
        (await button.Locator(".spinner-sm").IsVisibleAsync()).ShouldBeTrue();
        (await button.Locator(".submit-arrow").IsVisibleAsync()).ShouldBeFalse();
        CspViolations.ShouldBeEmpty();
    }

    /// <summary>
    /// The browser refuses an empty required field before the submit event the script listens for.
    /// </summary>
    [Fact]
    public async Task Login_still_signs_in_after_a_click_the_browser_refused_for_empty_fields()
    {
        // Arrange
        TestUser user = TestUser.CreateRandom();
        await AuthActions.RegisterAsync(Page, Fixture, user);
        await AuthActions.ConfirmEmailAsync(Page, Fixture, user);
        await Page.GotoAsync($"{Fixture.FrontendBaseUrl}/");
        await Page.GetByRole(AriaRole.Link, new() { Name = Links.Login, Exact = true }).ClickAsync();
        ILocator loginButton = Page.GetByRole(AriaRole.Button, new() { Name = Buttons.Login, Exact = true });
        PostWatch posts = PostWatch.StartCounting(Page);

        // Act: the first click has empty fields, so the browser sends nothing
        await loginButton.ClickAsync();
        await Page.GetByLabel(FieldLabels.Email).FillAsync(user.Email);
        await Page.GetByLabel(FieldLabels.Password, new() { Exact = true }).FillAsync(user.Password);
        await loginButton.ClickAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = Buttons.Logout, Exact = true }).WaitForAsync(LongWait);

        // Assert
        posts.PostsTo(LoginPath).ShouldBe(1);
        CspViolations.ShouldBeEmpty();
    }

}
