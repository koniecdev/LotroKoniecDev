using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

public sealed partial class LoginPageTests : EndpointsTestBase
{
    /// <summary>Where a sign-in with no usable continuation lands.</summary>
    private const string ExpectedFrontendLoginUrl = AuthSystemApiFactory.TestFrontendAppRoot + "/auth/login";

    public LoginPageTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    /// <summary>
    /// ADR-0046: this case happens only after the password was checked, so it names the real problem and
    /// carries the address to the resend page, the only thing that unblocks the account.
    /// </summary>
    [Fact]
    public async Task LoginPage_ShouldNameTheUnconfirmedAccount_WhenThePasswordIsCorrect()
    {
        // Arrange
        (RegisterRequest request, _) =
            await UserFactory.RegisterRandomUserUnconfirmedAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        // Act
        HttpResponseMessage response = await PostToLoginPageAsync(new Dictionary<string, string>
        {
            ["Email"] = request.Email,
            ["Password"] = request.Password
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("To konto nie zostało jeszcze aktywowane");
        html.ShouldNotContain("Nieprawidłowy e-mail lub hasło");
        html.ShouldContain($"/Account/ResendConfirmation?email={Uri.EscapeDataString(request.Email)}");
    }

    /// <summary>
    /// A caller without the password is not offered the resend link either. The footer always has a plain
    /// <c>/Account/ResendConfirmation</c>; only the link with an address says anything about an account.
    /// </summary>
    [Fact]
    public async Task LoginPage_ShouldNotOfferTheResendLink_WhenTheUnconfirmedAccountsPasswordIsWrong()
    {
        // Arrange
        (RegisterRequest request, _) =
            await UserFactory.RegisterRandomUserUnconfirmedAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        // Act
        HttpResponseMessage response = await PostToLoginPageAsync(new Dictionary<string, string>
        {
            ["Email"] = request.Email,
            ["Password"] = request.Password + "WRONG"
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string html = await response.Content.ReadAsStringAsync();
        html.ShouldNotContain("/Account/ResendConfirmation?email=");
    }

    [Fact]
    public async Task LoginPage_ShouldRejectLogin_WhenIdentifierIsUsernameInsteadOfEmail()
    {
        // Arrange: a confirmed account; the login identifier is the e-mail (ADR-0022)
        (RegisterRequest request, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        // Act: the valid username with the valid password must behave like any wrong credential
        HttpResponseMessage response = await PostToLoginPageAsync(new Dictionary<string, string>
        {
            ["Email"] = request.Username,
            ["Password"] = request.Password
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("Nieprawidłowy e-mail lub hasło");
    }

    /// <summary>
    /// ADR-0046: nothing about an address can be learned without its password. The two cases that need the
    /// right password, a scheduled deletion and an unconfirmed address, are pinned separately.
    /// </summary>
    [Fact]
    public async Task LoginPage_ShouldReturnIdenticalMessage_ForEveryFailureReachableWithoutThePassword()
    {
        // Arrange: a confirmed account (wrong-password + lockout branches) and an unconfirmed one
        (RegisterRequest confirmed, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);
        (RegisterRequest unconfirmed, _) =
            await UserFactory.RegisterRandomUserUnconfirmedAsync(ApiClient, Faker, AccountConfirmationEmailSpy);
        (RegisterRequest lockedOut, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);
        await AccountStateFactory.LockOutAsync(Factory.Services, lockedOut.Email);
        (RegisterRequest passwordless, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);
        await AccountStateFactory.RemovePasswordAsync(Factory.Services, passwordless.Email);

        // Act: one probe per credential-failure branch a caller can reach without the password
        string nonExistentMessage = await PostAndExtractRenderedAlertAsync(new Dictionary<string, string>
        {
            ["Email"] = "nobody-" + Faker.Random.AlphaNumeric(8) + "@example.com",
            ["Password"] = "WhateverPass1!"
        });
        string wrongPasswordMessage = await PostAndExtractRenderedAlertAsync(new Dictionary<string, string>
        {
            ["Email"] = confirmed.Email,
            ["Password"] = confirmed.Password + "WRONG"
        });
        string unconfirmedWrongPasswordMessage = await PostAndExtractRenderedAlertAsync(new Dictionary<string, string>
        {
            ["Email"] = unconfirmed.Email,
            ["Password"] = unconfirmed.Password + "WRONG"
        });
        string lockedOutMessage = await PostAndExtractRenderedAlertAsync(new Dictionary<string, string>
        {
            ["Email"] = lockedOut.Email,
            ["Password"] = lockedOut.Password // the right password: the lockout branch must still win
        });
        string passwordlessMessage = await PostAndExtractRenderedAlertAsync(new Dictionary<string, string>
        {
            ["Email"] = passwordless.Email,
            ["Password"] = passwordless.Password
        });

        // Assert: no branch reveals which check failed: identical text is the anti-enumeration invariant
        nonExistentMessage.ShouldNotBeNullOrWhiteSpace();
        wrongPasswordMessage.ShouldBe(nonExistentMessage);
        unconfirmedWrongPasswordMessage.ShouldBe(nonExistentMessage);
        lockedOutMessage.ShouldBe(nonExistentMessage);
        passwordlessMessage.ShouldBe(nonExistentMessage);
    }

    /// <summary>
    /// With identical texts, time is the only thing left that could tell the failures apart, so each one
    /// verifies exactly one hash, the dummy one where there is none. The no-password row is the seeded admin
    /// before its first reset (ADR-0056). The deletion rows take the branch before the lockout check,
    /// because a scheduled deletion also locks the account.
    /// </summary>
    [Theory]
    [InlineData("unknown address")]
    [InlineData("locked out")]
    [InlineData("no password")]
    [InlineData("wrong password")]
    [InlineData("deletion scheduled, wrong password")]
    [InlineData("deletion scheduled, no password")]
    public async Task LoginPage_ShouldVerifyExactlyOnePasswordHash_OnEveryFailureReachableWithoutThePassword(
        string failure)
    {
        // Arrange
        Dictionary<string, string> credentials = await ArrangeFailedLoginAsync(failure);
        SpyPasswordHasher passwordHasher = Factory.Services.GetRequiredService<SpyPasswordHasher>();
        passwordHasher.Reset();

        // Act
        HttpResponseMessage response = await PostToLoginPageAsync(credentials);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        passwordHasher.VerifyCount.ShouldBe(1);
    }

    /// <summary>
    /// A refused attempt against a scheduled deletion must cost what a wrong password costs, and must not
    /// touch the account's real hash: checking it would do the very work the budget refuses (#881).
    /// </summary>
    [Fact]
    public async Task LoginPage_ShouldVerifyOnlyTheDummyHash_WhenTheDeletionScheduledLoginBudgetIsSpent()
    {
        // Arrange
        (RegisterRequest user, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);
        await AccountStateFactory.ScheduleDeletionAsync(Factory.Services, user.Email);
        await AccountStateFactory.SpendDeletionScheduledLoginBudgetAsync(Factory.Services, user.Email);
        SpyPasswordHasher passwordHasher = Factory.Services.GetRequiredService<SpyPasswordHasher>();
        passwordHasher.Reset();

        // Act: the right password
        HttpResponseMessage response = await PostToLoginPageAsync(new Dictionary<string, string>
        {
            ["Email"] = user.Email,
            ["Password"] = user.Password
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        passwordHasher.VerifyCount.ShouldBe(1);
        passwordHasher.DummyVerifyCount.ShouldBe(1);
    }

    /// <summary>
    /// Every target outside this site is replaced by the frontend. In the <c>%09</c> row, a check that looks
    /// only at the first characters calls the target local, and <c>LocalRedirect</c> would then answer 500.
    /// </summary>
    [Theory]
    [InlineData("/\t/evil.example")]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("https://evil.example/harvest")]
    public async Task LoginPage_ShouldRedirectToTheFrontend_WhenReturnUrlIsNotLocal(string returnUrl)
    {
        // Arrange: a confirmed account, so the login itself succeeds and reaches the redirect
        (RegisterRequest request, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        // Act
        HttpResponseMessage response = await PostToLoginPageAsync(
            new Dictionary<string, string>
            {
                ["Email"] = request.Email,
                ["Password"] = request.Password
            },
            returnUrl);

        // Assert: the off-site target is dropped; the fallback comes from configuration, not the query
        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        response.Headers.Location.ShouldNotBeNull();
        response.Headers.Location!.OriginalString.ShouldBe(ExpectedFrontendLoginUrl);
    }

    /// <summary>
    /// The breach check runs only where a password is set (ADR-0066). A password that shows up in a leak
    /// after it was set must not lock its owner out.
    /// </summary>
    [Fact]
    public async Task LoginPage_ShouldStillSignIn_WhenThePasswordAppearsInDataBreachesAfterItWasSet()
    {
        // Arrange
        (RegisterRequest request, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);
        Factory.PwnedPasswords.MarkBreached(request.Password);

        // Act
        HttpResponseMessage response = await PostToLoginPageAsync(new Dictionary<string, string>
        {
            ["Email"] = request.Email,
            ["Password"] = request.Password
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        response.Headers.Location!.OriginalString.ShouldBe(ExpectedFrontendLoginUrl);
    }

    /// <summary>
    /// The reset-password and confirm-email pages send the user to a plain <c>/Account/Login</c>. This host's
    /// root is a dead end for a browser, so the fallback is the frontend's login route, where the new cookie
    /// completes the OIDC challenge without the user noticing.
    /// </summary>
    [Fact]
    public async Task LoginPage_ShouldRedirectToTheFrontend_WhenThereIsNoReturnUrl()
    {
        // Arrange: a confirmed account, mirroring a user who just reset their password
        (RegisterRequest request, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        // Act
        HttpResponseMessage response = await PostToLoginPageAsync(new Dictionary<string, string>
        {
            ["Email"] = request.Email,
            ["Password"] = request.Password
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        response.Headers.Location.ShouldNotBeNull();
        response.Headers.Location!.OriginalString.ShouldBe(ExpectedFrontendLoginUrl);
    }

    [Fact]
    public async Task LoginPage_ShouldResumeTheContinuation_WhenReturnUrlIsLocal()
    {
        // Arrange: the interrupted authorization the login flow is expected to resume
        const string continuation = "/connect/authorize?client_id=lotrokoniecdev-test&response_type=code";
        (RegisterRequest request, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        // Act
        HttpResponseMessage response = await PostToLoginPageAsync(
            new Dictionary<string, string>
            {
                ["Email"] = request.Email,
                ["Password"] = request.Password
            },
            continuation);

        // Assert: hardening must not break the flow it protects
        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        response.Headers.Location.ShouldNotBeNull();
        response.Headers.Location!.OriginalString.ShouldBe(continuation);
    }

    /// <summary>
    /// #681: the check keeps this local path, and the attribute encoding is what makes it harmless, so the
    /// proof reads the rendered body.
    /// </summary>
    [Theory]
    [InlineData("""/x" onfocus="alert(1)""", "/x&quot; onfocus=&quot;alert(1)")]
    [InlineData("/x<script>alert(1)</script>", "/x&lt;script&gt;alert(1)&lt;/script&gt;")]
    public async Task LoginPage_ShouldEncodeTheReturnUrl_WhenALocalPathCarriesHtmlCharacters(
        string returnUrl,
        string expectedAttributeValue)
    {
        // Act
        HttpResponseMessage response = await GetLoginPageAsync(returnUrl);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain($"""<input type="hidden" name="returnUrl" value="{expectedAttributeValue}" />""");
        html.ShouldNotContain(returnUrl);
    }

    /// <summary>
    /// The form tag helper leaves the antiforgery token out only when the markup sets an action itself
    /// (#681). Without the token every login POST would answer 400.
    /// </summary>
    [Fact]
    public async Task LoginPage_ShouldStillEmitTheAntiforgeryToken_WhenTheFormCarriesNoAction()
    {
        // Act
        HttpResponseMessage response = await GetLoginPageAsync("/connect/authorize?client_id=web");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string html = await response.Content.ReadAsStringAsync();
        ExtractAntiForgeryToken(html).ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// A target the check rejects must leave no trace in the page, so the field is rendered only when
    /// there is something to continue to.
    /// </summary>
    [Theory]
    [InlineData("//evil.example")]
    [InlineData("/\t/evil.example")]
    [InlineData("https://evil.example/harvest")]
    public async Task LoginPage_ShouldNotRenderTheHiddenField_WhenReturnUrlIsNotLocal(string returnUrl)
    {
        // Act
        HttpResponseMessage response = await GetLoginPageAsync(returnUrl);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string html = await response.Content.ReadAsStringAsync();
        html.ShouldNotContain("name=\"returnUrl\"");
    }

    /// <summary>
    /// Only the hidden field carries the continuation (#681). The POST goes to the bare page path, so a
    /// regression that drops the field cannot hide behind the query string.
    /// </summary>
    [Fact]
    public async Task LoginPage_ShouldResumeTheContinuation_WhenOnlyTheRenderedFormCarriesIt()
    {
        // Arrange
        const string continuation = "/connect/authorize?client_id=lotrokoniecdev-test&response_type=code";
        (RegisterRequest request, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        using HttpClient browser = Factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        string renderedPage = await GetLoginPageBodyAsync(browser, continuation);

        Match hiddenField = HiddenReturnUrlRegex().Match(renderedPage);
        hiddenField.Success.ShouldBeTrue("The login form must carry the return target in a hidden field.");

        // Act
        HttpResponseMessage response = await PostLoginFormAsync(
            browser,
            renderedPage,
            new Dictionary<string, string>
            {
                ["Email"] = request.Email,
                ["Password"] = request.Password,
                ["returnUrl"] = WebUtility.HtmlDecode(hiddenField.Groups[1].Value)
            });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        response.Headers.Location.ShouldNotBeNull();
        response.Headers.Location!.OriginalString.ShouldBe(continuation);
    }

    /// <summary>
    /// As a browser posts: the action-less form goes back to the page's address, so the query string
    /// arrives with the hidden field. Model binding reads the form first.
    /// </summary>
    [Fact]
    public async Task LoginPage_ShouldResumeTheContinuation_WhenTheQueryAndTheFormBothCarryIt()
    {
        // Arrange
        const string continuation = "/connect/authorize?client_id=lotrokoniecdev-test&response_type=code";
        (RegisterRequest request, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        using HttpClient browser = Factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        string renderedPage = await GetLoginPageBodyAsync(browser, continuation);

        Match hiddenField = HiddenReturnUrlRegex().Match(renderedPage);
        hiddenField.Success.ShouldBeTrue("The login form must carry the return target in a hidden field.");

        // Act
        HttpResponseMessage response = await PostLoginFormAsync(
            browser,
            renderedPage,
            new Dictionary<string, string>
            {
                ["Email"] = request.Email,
                ["Password"] = request.Password,
                ["returnUrl"] = WebUtility.HtmlDecode(hiddenField.Groups[1].Value)
            },
            $"/Account/Login?returnUrl={Uri.EscapeDataString(continuation)}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        response.Headers.Location.ShouldNotBeNull();
        response.Headers.Location!.OriginalString.ShouldBe(continuation);
    }

    private async Task<Dictionary<string, string>> ArrangeFailedLoginAsync(string failure)
    {
        if (failure is "unknown address")
        {
            return new Dictionary<string, string>
            {
                ["Email"] = "nobody-" + Faker.Random.AlphaNumeric(8) + "@example.com",
                ["Password"] = "WhateverPass1!"
            };
        }

        (RegisterRequest user, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);
        string password = user.Password;

        switch (failure)
        {
            case "locked out":
                await AccountStateFactory.LockOutAsync(Factory.Services, user.Email);
                break;
            case "no password":
                await AccountStateFactory.RemovePasswordAsync(Factory.Services, user.Email);
                break;
            case "wrong password":
                password += "WRONG";
                break;
            case "deletion scheduled, wrong password":
                await AccountStateFactory.ScheduleDeletionAsync(Factory.Services, user.Email);
                password += "WRONG";
                break;
            case "deletion scheduled, no password":
                await AccountStateFactory.RemovePasswordAsync(Factory.Services, user.Email);
                await AccountStateFactory.ScheduleDeletionAsync(Factory.Services, user.Email);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(failure), failure, null);
        }

        return new Dictionary<string, string>
        {
            ["Email"] = user.Email,
            ["Password"] = password
        };
    }

    private async Task<string> PostAndExtractRenderedAlertAsync(Dictionary<string, string> formFields)
    {
        HttpResponseMessage response = await PostToLoginPageAsync(formFields);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        string html = await response.Content.ReadAsStringAsync();
        Match match = AlertMessageRegex().Match(html);
        match.Success.ShouldBeTrue("Expected the login page to render an error alert.");
        return match.Groups[1].Value.Trim();
    }

    /// <summary>
    /// One client for the GET and the POST, so the antiforgery cookie carries over, and it does not follow
    /// redirects, so the target stays in the <c>Location</c> header.
    /// </summary>
    private async Task<HttpResponseMessage> PostToLoginPageAsync(
        Dictionary<string, string> formFields,
        string? returnUrl = null)
    {
        using HttpClient browser = Factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage pageResponse = await browser.GetAsync(
            new Uri("/Account/Login", UriKind.Relative));
        pageResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        string html = await pageResponse.Content.ReadAsStringAsync();
        string? antiForgeryToken = ExtractAntiForgeryToken(html);
        if (antiForgeryToken is not null)
        {
            formFields["__RequestVerificationToken"] = antiForgeryToken;
        }

        string postUrl = returnUrl is null
            ? "/Account/Login"
            : $"/Account/Login?returnUrl={Uri.EscapeDataString(returnUrl)}";

        using FormUrlEncodedContent content = new(formFields);
        using HttpRequestMessage request = new(HttpMethod.Post, postUrl);
        request.Content = content;

        return await browser.SendAsync(request);
    }

    private async Task<HttpResponseMessage> GetLoginPageAsync(string returnUrl) =>
        await ApiClient.Http.GetAsync(
            new Uri($"/Account/Login?returnUrl={Uri.EscapeDataString(returnUrl)}", UriKind.Relative));

    private static async Task<string> GetLoginPageBodyAsync(HttpClient browser, string returnUrl)
    {
        HttpResponseMessage pageResponse = await browser.GetAsync(
            new Uri($"/Account/Login?returnUrl={Uri.EscapeDataString(returnUrl)}", UriKind.Relative));
        pageResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await pageResponse.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// <paramref name="postUrl"/> defaults to the bare page path, where a test sends the form when the
    /// hidden field alone has to carry the target.
    /// </summary>
    private static async Task<HttpResponseMessage> PostLoginFormAsync(
        HttpClient browser,
        string renderedPage,
        Dictionary<string, string> formFields,
        string postUrl = "/Account/Login")
    {
        if (ExtractAntiForgeryToken(renderedPage) is { } antiForgeryToken)
        {
            formFields["__RequestVerificationToken"] = antiForgeryToken;
        }

        using FormUrlEncodedContent content = new(formFields);
        using HttpRequestMessage request = new(HttpMethod.Post, postUrl);
        request.Content = content;

        return await browser.SendAsync(request);
    }

    private static string? ExtractAntiForgeryToken(string html)
    {
        Match match = AntiForgeryTokenRegex().Match(html);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex("""name="__RequestVerificationToken".*?value="([^"]+)""")]
    private static partial Regex AntiForgeryTokenRegex();

    [GeneratedRegex("""name="returnUrl"[^>]*value="([^">]*)""")]
    private static partial Regex HiddenReturnUrlRegex();

    [GeneratedRegex("""<span class="s">(.*?)</span>""", RegexOptions.Singleline)]
    private static partial Regex AlertMessageRegex();
}
