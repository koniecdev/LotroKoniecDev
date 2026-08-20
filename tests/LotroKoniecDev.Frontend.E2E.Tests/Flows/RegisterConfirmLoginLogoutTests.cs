using LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Shouldly;

namespace LotroKoniecDev.Frontend.E2E.Tests.Flows;

/// <summary>
/// The happy path: a new visitor registers on the auth server, confirms their e-mail through the link
/// Mailpit received, logs in through the frontend's OIDC challenge and logs out. That proves the whole
/// onboarding loop across both origins, the frontend and the auth server.
/// Nothing has to be seeded and no <c>exported.txt</c> is needed: the user, the e-mail and the session
/// are all created by the flow.
/// </summary>
public sealed class RegisterConfirmLoginLogoutTests : E2ETestBase
{
    public RegisterConfirmLoginLogoutTests(PlaywrightStackFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Register_then_confirm_email_then_login_then_logout()
    {
        // Arrange
        TestUser user = TestUser.CreateRandom();

        // Act and assert: registration shows the "check your email" panel.
        await AuthActions.RegisterAsync(Page, Fixture, user);
        (await ByTestId(TestIds.RegisterSuccess).IsVisibleAsync()).ShouldBeTrue();

        // Confirm through the link in Mailpit, and the auth page reports success.
        await AuthActions.ConfirmEmailAsync(Page, Fixture, user);
        (await ByTestId(TestIds.ConfirmEmailSuccess).IsVisibleAsync()).ShouldBeTrue();

        // Log in through the frontend's OIDC challenge, and the logged-in navigation, with its logout
        // button, appears.
        await AuthActions.LoginAsync(Page, Fixture, user);
        (await Page.GetByRole(AriaRole.Button, new() { Name = Buttons.Logout, Exact = true }).IsVisibleAsync())
            .ShouldBeTrue();

        // The navigation greets the user by their username, from the `name` claim, and not by the e-mail
        // they logged in with. That is the "username is only a display handle" half of ADR-0022, shown
        // across the whole OIDC loop.
        (await Page.GetByText(user.Username).IsVisibleAsync()).ShouldBeTrue();

        // Log out, and the anonymous navigation with its login link comes back.
        await AuthActions.LogoutAsync(Page);
        (await Page.GetByRole(AriaRole.Link, new() { Name = Links.Login, Exact = true }).IsVisibleAsync())
            .ShouldBeTrue();
    }
}
