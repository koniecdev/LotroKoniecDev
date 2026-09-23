using LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Shouldly;

namespace LotroKoniecDev.Frontend.E2E.Tests.Flows;

/// <summary>
/// The auth origin sends a strict CSP (#693): inline styles only with the response's nonce, and no
/// inline script at all. A page that breaks it still returns 200 and still loads, just unstyled or
/// without its script, so only a real browser can tell (#670). This opens every account page in one
/// and checks that nothing was blocked.
/// It needs no account and nothing seeded. The two link pages are also opened with a link that parses,
/// so the state that renders the form is checked too.
/// </summary>
public sealed class AuthPagesContentSecurityPolicyTests : E2ETestBase
{
    private const string DummyLinkQuery = "?email=a%40b.pl&token=z";

    public AuthPagesContentSecurityPolicyTests(PlaywrightStackFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Every_account_page_renders_under_its_own_csp_without_a_violation()
    {
        // Arrange
        string[] pages =
        [
            "/Account/Login",
            "/Account/Register",
            "/Account/ForgotPassword",
            "/Account/ResendConfirmation",
            "/Account/ResetPassword",
            "/Account/PrivacyPolicy",
            "/Account/ConfirmEmail",
            "/Account/ConfirmEmailChange",
            "/Account/RevertEmailChange",
            "/Account/RevertEmailChange?userId=00000000-0000-0000-0000-000000000001&from=a%40b.pl&to=c%40d.pl&token=z",
            "/Account/CancelDeletion",
            "/Account/CancelDeletion" + DummyLinkQuery
        ];
        List<string> unstyledPages = [];

        // Act
        foreach (string page in pages)
        {
            IResponse? response = await Page.GotoAsync(
                Fixture.AuthBaseUrl + page, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            response.ShouldNotBeNull();
            response.Headers.ShouldContainKey("content-security-policy");

            // A style block the CSP refused gets no stylesheet at all, so this also catches a violation
            // the listener missed.
            bool everyStyleApplied = await Page.EvaluateAsync<bool>(
                "() => [...document.querySelectorAll('style')].every(style => style.sheet !== null)");
            if (!everyStyleApplied)
            {
                unstyledPages.Add(page);
            }
        }

        // Assert
        unstyledPages.ShouldBeEmpty();
        CspViolations.ShouldBeEmpty();
    }

    /// <summary>
    /// The listener behind <see cref="E2ETestBase.CspViolations"/> is what every other CSP check in this
    /// suite relies on. If it stopped firing, they would all stay green, so this proves it still reports.
    /// </summary>
    [Fact]
    public async Task Violation_listener_reports_a_style_the_csp_blocks()
    {
        // Arrange
        await Page.GotoAsync(Fixture.AuthBaseUrl + "/Account/Login");

        // Act: a style block without the response's nonce, which style-src refuses
        bool injectedStyleApplied = await Page.EvaluateAsync<bool>(
            """
            () => {
                const style = document.createElement('style');
                style.textContent = 'body { outline: 9px solid red; }';
                document.head.appendChild(style);
                return style.sheet !== null;
            }
            """);

        // Assert: the report comes back through a binding, so give it a moment to arrive
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (CspViolations.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Page.WaitForTimeoutAsync(100);
        }

        injectedStyleApplied.ShouldBeFalse();
        CspViolations.ShouldContain(violation => violation.Contains("style-src", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Login_page_script_still_toggles_the_password_under_the_csp()
    {
        // Arrange: the toggle used to be an inline script, which this CSP blocks; it now comes from a file
        await Page.GotoAsync(Fixture.AuthBaseUrl + "/Account/Login");
        ILocator password = Page.GetByLabel(FieldLabels.Password, new() { Exact = true });

        // Act
        await Page.GetByRole(AriaRole.Button, new() { Name = "Pokaż hasło", Exact = true }).ClickAsync();

        // Assert
        (await password.GetAttributeAsync("type")).ShouldBe("text");
        CspViolations.ShouldBeEmpty();
    }
}
