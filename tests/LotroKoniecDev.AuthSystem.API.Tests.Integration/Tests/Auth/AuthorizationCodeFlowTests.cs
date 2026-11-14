using System.Buffers.Text;
using System.Collections.Specialized;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Npgsql;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using LotroKoniecDev.AuthSystem.API.BackgroundServices;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using Microsoft.EntityFrameworkCore;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.Tests.Shared;
using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

[Collection("AuthApi")]
public sealed partial class AuthorizationCodeFlowTests : AsyncLifetimeTestBase
{
    private const string PostLogoutRedirectUri = "https://localhost:5001";
    private const string RedirectUri = "https://localhost:5001/callback";

    /// <summary>
    /// The website's confidential client (ADR-0074). Every request the website sends carries its secret.
    /// </summary>
    private const string WebsiteClientId = AuthConstants.ClientIds.Frontend;
    private const string WebsiteClientSecret = AuthSystemApiFactory.TestFrontendClientSecret;
    private const string WrongClientSecret = "a-wrong-secret-that-is-long-enough-for-the-check";

    /// <summary>
    /// The old public client stays for one release (ADR-0074) and sends no secret.
    /// </summary>
    private const string OldPublicClientId = AuthConstants.ClientIds.Web;

    protected override TestApiClient ApiClient { get; }

    private readonly HttpClient _noRedirectClient;

    public AuthorizationCodeFlowTests(AuthSystemApiFactory appFactory) : base(appFactory)
    {
        JsonSerializerOptions jsonSerializerOptions =
            appFactory.Services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;

        ApiClient = new TestApiClient(appFactory.CreateClient(), jsonSerializerOptions);

        _noRedirectClient = appFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task Authorize_ShouldRedirectToLogin_WhenUserIsNotAuthenticated()
    {
        // Arrange
        (_, string codeChallenge) = GeneratePkce();
        string authorizeUrl = BuildAuthorizeUrl(codeChallenge);

        // Act
        HttpResponseMessage response = await _noRedirectClient.GetAsync(
            new Uri(authorizeUrl, UriKind.Relative));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);

        string? location = response.Headers.Location?.ToString();
        location.ShouldNotBeNull();
        location.ShouldContain("/Account/Login");
        location.ShouldContain("ReturnUrl=");
    }

    /// <summary>
    /// The browser reaches this endpoint directly, not through an account page, so it must still get the
    /// Polish error page (#867).
    /// </summary>
    [Fact]
    public async Task Authorize_ShouldShowABrowserThePolishErrorPage_WhenTheDatabaseFails()
    {
        // Arrange: a permanent error, because the execution strategy would replay a passing one and succeed
        (_, string codeChallenge) = GeneratePkce();
        Factory.DbCommandFailures.FailNext(
            command => command.CommandText.Contains("\"OpenIddictApplications\"", StringComparison.Ordinal),
            () => new PostgresException(
                "simulated permanent failure", "ERROR", "ERROR", PostgresErrorCodes.UndefinedTable));

        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(BuildAuthorizeUrl(codeChallenge), UriKind.Relative));
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

        // Act
        using HttpResponseMessage response = await _noRedirectClient.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        Factory.DbCommandFailures.FailuresInjected.ShouldBe(1);
        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe("text/html");
        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("<h1>Coś poszło nie tak</h1>");
    }

    [Fact]
    public async Task FullAuthorizationCodeFlow_ShouldReturnTokens()
    {
        // Arrange
        const string password = "TestPass1!";
        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, password);

        (string codeVerifier, string codeChallenge) = GeneratePkce();
        string authorizeUrl = BuildAuthorizeUrl(codeChallenge);

        // Step 1: Hit /connect/authorize - should redirect to login
        HttpResponseMessage authorizeResponse = await _noRedirectClient.GetAsync(
            new Uri(authorizeUrl, UriKind.Relative));

        authorizeResponse.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        string loginRedirect = authorizeResponse.Headers.Location!.ToString();
        loginRedirect.ShouldContain("/Account/Login");

        // Step 2: GET the login page
        HttpResponseMessage loginPageResponse = await _noRedirectClient.GetAsync(
            new Uri(loginRedirect, UriKind.RelativeOrAbsolute));

        loginPageResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        string loginPageHtml = await loginPageResponse.Content.ReadAsStringAsync();
        loginPageHtml.ShouldContain("Zaloguj się");

        string? antiForgeryToken = ExtractAntiForgeryToken(loginPageHtml);
        IEnumerable<string> setCookieHeaders =
            loginPageResponse.Headers.GetValues("Set-Cookie");

        // Step 3: POST login credentials
        string? returnUrl = ExtractReturnUrl(loginRedirect);

        Dictionary<string, string> loginFormData = new()
        {
            ["Email"] = registerRequest.Email,
            ["Password"] = password,
        };

        if (antiForgeryToken is not null)
        {
            loginFormData["__RequestVerificationToken"] = antiForgeryToken;
        }

        using FormUrlEncodedContent loginContent = new(loginFormData);

        string loginPostUrl = returnUrl is not null
            ? $"/Account/Login?returnUrl={Uri.EscapeDataString(returnUrl)}"
            : "/Account/Login";

        using HttpRequestMessage loginPostRequest = new(HttpMethod.Post, loginPostUrl);
        loginPostRequest.Content = loginContent;

        foreach (string cookie in setCookieHeaders)
        {
            string cookieName = cookie.Split('=')[0];
            string cookieValue = cookie.Split('=')[1].Split(';')[0];
            loginPostRequest.Headers.Add("Cookie", $"{cookieName}={cookieValue}");
        }

        HttpResponseMessage loginResponse = await _noRedirectClient.SendAsync(loginPostRequest);

        // Should redirect back to authorize endpoint
        loginResponse.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        string postLoginRedirect = loginResponse.Headers.Location!.ToString();

        IEnumerable<string> authCookies = loginResponse.Headers
            .Where(h => h.Key == "Set-Cookie")
            .SelectMany(h => h.Value);

        // Step 4: Follow redirect back to /connect/authorize (now with auth cookie)
        using HttpRequestMessage authorizeWithCookieRequest = new(HttpMethod.Get, postLoginRedirect);
        foreach (string cookie in authCookies)
        {
            string cookiePair = cookie.Split(';')[0];
            authorizeWithCookieRequest.Headers.Add("Cookie", cookiePair);
        }

        HttpResponseMessage authorizeWithCookieResponse =
            await _noRedirectClient.SendAsync(authorizeWithCookieRequest);

        // Should redirect to the client's redirect_uri with an authorization code
        authorizeWithCookieResponse.StatusCode.ShouldBe(HttpStatusCode.Redirect);

        string callbackUrl = authorizeWithCookieResponse.Headers.Location!.ToString();
        callbackUrl.ShouldContain("https://localhost:5001/callback");
        callbackUrl.ShouldContain("code=");

        Uri callbackUri = new(callbackUrl);
        NameValueCollection queryParams = HttpUtility.ParseQueryString(callbackUri.Query);
        string authorizationCode = queryParams["code"]!;
        authorizationCode.ShouldNotBeNullOrEmpty();

        // Step 5: Exchange authorization code for tokens
        using FormUrlEncodedContent tokenRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = authorizationCode,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = WebsiteClientId,
            ["client_secret"] = WebsiteClientSecret,
            ["code_verifier"] = codeVerifier
        });

        HttpResponseMessage tokenResponse = await ApiClient.Http.PostAsync(
            new Uri("connect/token", UriKind.Relative), tokenRequest);

        // Assert
        tokenResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        string tokenContent = await tokenResponse.Content.ReadAsStringAsync();
        using JsonDocument tokenJson = JsonDocument.Parse(tokenContent);

        tokenJson.RootElement.GetProperty("access_token").GetString().ShouldNotBeNullOrEmpty();
        tokenJson.RootElement.GetProperty("refresh_token").GetString().ShouldNotBeNullOrEmpty();
        tokenJson.RootElement.GetProperty("id_token").GetString().ShouldNotBeNullOrEmpty();
        tokenJson.RootElement.GetProperty("token_type").GetString().ShouldBe("Bearer");
        tokenJson.RootElement.GetProperty("expires_in").GetInt32().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Authorize_WithPromptNone_ShouldReturnError_WhenNotAuthenticated()
    {
        // Arrange
        (_, string codeChallenge) = GeneratePkce();
        string authorizeUrl = BuildAuthorizeUrl(codeChallenge) + "&prompt=none";

        // Act
        HttpResponseMessage response = await _noRedirectClient.GetAsync(
            new Uri(authorizeUrl, UriKind.Relative));

        // Assert - should redirect to callback with error
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);

        string? location = response.Headers.Location?.ToString();
        location.ShouldNotBeNull();
        location.ShouldContain("error=login_required");
    }

    [Fact]
    public async Task LoginPage_ShouldReturnOk_WhenAccessed()
    {
        // Act
        HttpResponseMessage response = await ApiClient.Http.GetAsync(
            new Uri("/Account/Login", UriKind.Relative));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("Zaloguj się");
        html.ShouldContain("Adres e-mail");
        html.ShouldContain("Hasło");
    }

    [Fact]
    public async Task LoginPage_ShouldShowError_WhenCredentialsAreInvalid()
    {
        // Arrange
        HttpResponseMessage loginPageResponse = await ApiClient.Http.GetAsync(
            new Uri("/Account/Login", UriKind.Relative));
        loginPageResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        string loginPageHtml = await loginPageResponse.Content.ReadAsStringAsync();
        string? antiForgeryToken = ExtractAntiForgeryToken(loginPageHtml);

        Dictionary<string, string> loginFormData = new()
        {
            ["Email"] = "nonexistent@example.com",
            ["Password"] = "WrongPassword1!"
        };

        if (antiForgeryToken is not null)
        {
            loginFormData["__RequestVerificationToken"] = antiForgeryToken;
        }

        using FormUrlEncodedContent content = new(loginFormData);

        using HttpRequestMessage request = new(HttpMethod.Post, "/Account/Login");
        request.Content = content;

        if (loginPageResponse.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies))
        {
            foreach (string cookie in cookies)
            {
                string cookiePair = cookie.Split(';')[0];
                request.Headers.Add("Cookie", cookiePair);
            }
        }

        // Act
        HttpResponseMessage response = await ApiClient.Http.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK); // Returns 200 with error message on page
        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("Nieprawidłowy e-mail lub hasło");
    }

    [Fact]
    public async Task Login_ShouldNotAuthenticate_WhenEmailIsNotConfirmed()
    {
        // Arrange: an unconfirmed user with the right password; RequireConfirmedEmail must reject it.
        const string password = "TestPass1!";
        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserUnconfirmedAsync(ApiClient, Faker, AccountConfirmationEmailSpy, password);

        HttpResponseMessage loginPageResponse = await _noRedirectClient.GetAsync(
            new Uri("/Account/Login", UriKind.Relative));
        loginPageResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        string loginPageHtml = await loginPageResponse.Content.ReadAsStringAsync();
        string? antiForgeryToken = ExtractAntiForgeryToken(loginPageHtml);

        Dictionary<string, string> loginFormData = new()
        {
            ["Email"] = registerRequest.Email,
            ["Password"] = password
        };

        if (antiForgeryToken is not null)
        {
            loginFormData["__RequestVerificationToken"] = antiForgeryToken;
        }

        using FormUrlEncodedContent content = new(loginFormData);

        using HttpRequestMessage request = new(HttpMethod.Post, "/Account/Login");
        request.Content = content;

        foreach (string cookie in loginPageResponse.Headers.GetValues("Set-Cookie"))
        {
            string cookiePair = cookie.Split(';')[0];
            request.Headers.Add("Cookie", cookiePair);
        }

        // Act
        HttpResponseMessage response = await _noRedirectClient.SendAsync(request);

        // Assert: the page is re-rendered (200) with the unconfirmed-account error (ADR-0046), not the 302
        // a successful sign-in gives.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("To konto nie zostało jeszcze aktywowane");
    }

    /// <summary>
    /// The right secret does not replace PKCE (ADR-0074). A missing verifier is refused before the client
    /// is checked, so only a wrong one proves this.
    /// </summary>
    [Fact]
    public async Task AuthorizationCodeExchange_ShouldFail_WhenCodeVerifierIsInvalid()
    {
        // Arrange
        (string authorizationCode, _, _, _) = await ObtainAuthorizationCodeAsync();

        string wrongCodeVerifier = "this-is-a-wrong-verifier-that-does-not-match-the-challenge";

        using FormUrlEncodedContent tokenRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = authorizationCode,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = WebsiteClientId,
            ["client_secret"] = WebsiteClientSecret,
            ["code_verifier"] = wrongCodeVerifier
        });

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsync(
            new Uri("connect/token", UriKind.Relative), tokenRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(response)).ShouldBe(OpenIddictConstants.Errors.InvalidGrant);
    }

    [Fact]
    public async Task AuthorizationCodeExchange_ShouldFail_WhenAuthorizationCodeIsExpired()
    {
        // Arrange
        (string authorizationCode, string codeVerifier, _, _) = await ObtainAuthorizationCodeAsync();

        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await dbContext.Database.ExecuteSqlRawAsync(
            """UPDATE authsystem."OpenIddictTokens" SET "ExpirationDate" = '2020-01-01 00:00:00+00' WHERE "Type" = 'urn:openiddict:params:oauth:token-type:authorization_code'""");

        using FormUrlEncodedContent tokenRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = authorizationCode,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = WebsiteClientId,
            ["client_secret"] = WebsiteClientSecret,
            ["code_verifier"] = codeVerifier
        });

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsync(
            new Uri("connect/token", UriKind.Relative), tokenRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AuthorizationCodeExchange_ShouldFail_WhenSecurityStampChangedAfterAuthorize()
    {
        // Arrange: a password reset lands between /connect/authorize and the code exchange (#848)
        (string authorizationCode, string codeVerifier, _, string email) = await ObtainAuthorizationCodeAsync();

        await AccountStateFactory.ChangeSecurityStampAsync(Factory.Services, email);

        // Act
        using HttpResponseMessage response = await ExchangeAuthorizationCodeAsync(authorizationCode, codeVerifier);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("error").GetString().ShouldBe("invalid_grant");
        body.RootElement.GetProperty("error_description").GetString().ShouldBe("The authorization code is no longer valid.");
    }

    [Fact]
    public async Task AuthorizationCodeExchange_ShouldFail_WhenTheAccountWasLockedOutAfterAuthorize()
    {
        // Arrange: a lockout does not change the stamp, so the exchange has to check it on its own
        (string authorizationCode, string codeVerifier, _, string email) = await ObtainAuthorizationCodeAsync();

        await using (AsyncServiceScope scope = Factory.Services.CreateAsyncScope())
        {
            UserManager<ApplicationUser> userManager =
                scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            ApplicationUser? user = await userManager.FindByEmailAsync(email);
            user.ShouldNotBeNull();
            (await userManager.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddMinutes(15))).Succeeded.ShouldBeTrue();
        }

        // Act
        using HttpResponseMessage response = await ExchangeAuthorizationCodeAsync(authorizationCode, codeVerifier);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("error").GetString().ShouldBe("invalid_grant");
        body.RootElement.GetProperty("error_description").GetString().ShouldBe("The authorization code is no longer valid.");
    }

    [Fact]
    public async Task AuthorizationCodeExchange_ShouldFail_WhenTheAccountWasScheduledForDeletionAfterAuthorize()
    {
        // Arrange: only the deletion date is set, so the stamp is unchanged and the exchange has to check
        // the date on its own
        (string authorizationCode, string codeVerifier, _, string email) = await ObtainAuthorizationCodeAsync();

        await AccountStateFactory.ScheduleDeletionAsync(Factory.Services, email);

        // Act
        using HttpResponseMessage response = await ExchangeAuthorizationCodeAsync(authorizationCode, codeVerifier);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe("application/json");
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("error").GetString().ShouldBe("invalid_grant");
        body.RootElement.GetProperty("error_description").GetString().ShouldBe("The authorization code is no longer valid.");
    }

    [Fact]
    public async Task AuthorizationCodeExchange_ShouldFail_WhenTheAccountWasDeletedAfterAuthorize()
    {
        // Arrange: an operator deletes the row by hand, as the runbook's admin fixes do, while a code is
        // still in flight
        (string authorizationCode, string codeVerifier, _, string email) = await ObtainAuthorizationCodeAsync();

        await AccountStateFactory.DeleteAsync(Factory.Services, email);

        // Act
        using HttpResponseMessage response = await ExchangeAuthorizationCodeAsync(authorizationCode, codeVerifier);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("error").GetString().ShouldBe("invalid_grant");
        body.RootElement.GetProperty("error_description").GetString().ShouldBe("The authorization code is no longer valid.");
    }

    [Fact]
    public async Task AuthorizationCodeFlow_ShouldIssueARefreshTokenThatRefreshes()
    {
        // Arrange: this is the frontend's sign-in. Its refresh token must carry the stamp, or every
        // browser session would end at its first refresh (#848).
        WebsiteSession session = await SignInThroughTheWebsiteAsync();

        // Act
        using HttpResponseMessage response = await RefreshAsync(session.RefreshToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AuthorizationCodeExchange_ShouldIssueTheWebsiteAnAccessTokenThatNamesBothApis()
    {
        // Arrange: the website sends this one token to the translation API and to this API's account
        // endpoints, and each of them takes only a token that names it (#1023)
        (string authorizationCode, string codeVerifier, _, _) = await ObtainAuthorizationCodeAsync();

        // Act
        using HttpResponseMessage response = await ExchangeAuthorizationCodeAsync(authorizationCode, codeVerifier);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using JsonDocument tokens = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JwtPayload.ReadAudiences(tokens.RootElement.GetProperty("access_token").GetString()!)
            .ShouldBe([AuthConstants.ClientIds.Api, AuthConstants.Audiences.AuthApi], ignoreOrder: true);
    }

    [Fact]
    public async Task RefreshTokenGrant_ShouldIssueTheWebsiteAnAccessTokenThatNamesBothApis()
    {
        // Arrange
        WebsiteSession session = await SignInThroughTheWebsiteAsync();

        // Act
        using HttpResponseMessage response = await RefreshAsync(session.RefreshToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using JsonDocument tokens = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JwtPayload.ReadAudiences(tokens.RootElement.GetProperty("access_token").GetString()!)
            .ShouldBe([AuthConstants.ClientIds.Api, AuthConstants.Audiences.AuthApi], ignoreOrder: true);
    }

    [Fact]
    public async Task AuthorizationCodeExchange_ShouldNameBothApis_WhenTheCodeWasIssuedWithoutTheAuthApi()
    {
        // Arrange: a code from /connect/authorize just before #1023 was deployed, redeemed just after it.
        // Codes are sealed with each host's own keys, so the code and the exchange use the same host.
        const string password = "TestPass1!";
        string email = await RegisterUserAsync(password);
        await using WebApplicationFactory<Program> host = CreateHostThatIssuesCodesWithoutTheAuthApi();
        (string authorizationCode, string codeVerifier, _) = await ObtainAuthorizationCodeForAsync(host, email, password);
        using HttpClient client = host.CreateClient();

        // Act
        using HttpResponseMessage response = await ExchangeAuthorizationCodeAsync(client, authorizationCode, codeVerifier);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using JsonDocument tokens = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JwtPayload.ReadAudiences(tokens.RootElement.GetProperty("access_token").GetString()!)
            .ShouldBe([AuthConstants.ClientIds.Api, AuthConstants.Audiences.AuthApi], ignoreOrder: true);
    }

    [Fact]
    public async Task AuthorizationCodeExchange_ShouldKeepTheSecurityStampOutOfTheAccessAndIdentityTokens()
    {
        // Arrange: access tokens are not encrypted and the stamp is server-side state, so it must stay
        // in the code and the refresh token, which only the server reads.
        (string authorizationCode, string codeVerifier, _, string email) = await ObtainAuthorizationCodeAsync();

        string securityStamp;
        await using (AsyncServiceScope scope = Factory.Services.CreateAsyncScope())
        {
            UserManager<ApplicationUser> userManager =
                scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            ApplicationUser? user = await userManager.FindByEmailAsync(email);
            user.ShouldNotBeNull();
            securityStamp = await userManager.GetSecurityStampAsync(user);
        }

        // Act
        using HttpResponseMessage response = await ExchangeAuthorizationCodeAsync(authorizationCode, codeVerifier);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string content = await response.Content.ReadAsStringAsync();
        using JsonDocument json = JsonDocument.Parse(content);
        JwtPayload.Read(json.RootElement.GetProperty("access_token").GetString()!).ShouldNotContain(securityStamp);
        JwtPayload.Read(json.RootElement.GetProperty("id_token").GetString()!).ShouldNotContain(securityStamp);
    }

    [Fact]
    public async Task AuthorizationCodeExchange_ShouldMintAnAccessTokenTypedAtJwt()
    {
        // Arrange: this is the frontend's own grant, and the TMS accepts only this type (#933).
        (string authorizationCode, string codeVerifier, _, _) = await ObtainAuthorizationCodeAsync();

        // Act
        using HttpResponseMessage response = await ExchangeAuthorizationCodeAsync(authorizationCode, codeVerifier);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string content = await response.Content.ReadAsStringAsync();
        using JsonDocument json = JsonDocument.Parse(content);
        string accessToken = json.RootElement.GetProperty("access_token").GetString().ShouldNotBeNull();

        new JsonWebToken(accessToken).Typ.ShouldBe(AuthConstants.TokenTypes.AccessToken);
    }

    [Fact]
    public async Task Logout_ShouldRedirectToPostLogoutRedirectUri_WhenIdTokenHintIsProvided()
    {
        // Arrange
        WebsiteSession session = await SignInThroughTheWebsiteAsync();

        // Act
        using HttpResponseMessage logoutResponse = await SignOutAsync(session.IdToken, session.AuthCookies);

        // Assert: Should redirect to the post_logout_redirect_uri
        logoutResponse.StatusCode.ShouldBe(HttpStatusCode.Redirect);

        string? location = logoutResponse.Headers.Location?.ToString();
        location.ShouldNotBeNull();
        location.ShouldStartWith(PostLogoutRedirectUri);
    }

    [Fact]
    public async Task Logout_ShouldRevokeTheRefreshToken_WhenOnlyTheIdTokenHintIsSent()
    {
        // Arrange: the website's sign-out once the sign-in server's own cookie has expired (#931)
        WebsiteSession session = await SignInThroughTheWebsiteAsync();

        using HttpResponseMessage logoutResponse = await SignOutAsync(session.IdToken, authCookies: []);
        logoutResponse.StatusCode.ShouldBe(HttpStatusCode.Redirect);

        // Act
        using HttpResponseMessage response = await RefreshAsync(session.RefreshToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("error").GetString().ShouldBe("invalid_grant");
    }

    [Fact]
    public async Task Logout_ShouldRevokeTheAuthorizationAndTheTokensOfTheSession_WhenOnlyTheIdTokenHintIsSent()
    {
        // Arrange
        WebsiteSession session = await SignInThroughTheWebsiteAsync();

        // Act
        using HttpResponseMessage response = await SignOutAsync(session.IdToken, authCookies: []);

        // Assert: the authorization is checked too, because a refresh that races the revoke can save its
        // own copy of a token row over it
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await OpenIddictTokenState.StatusOfAsync(Factory.Services, session.RefreshToken))
            .ShouldBe(OpenIddictConstants.Statuses.Revoked);
        List<string?> authorizationStatuses =
            await OpenIddictTokenState.AuthorizationStatusesOfAsync(Factory.Services, session.UserId);
        authorizationStatuses.ShouldHaveSingleItem().ShouldBe(OpenIddictConstants.Statuses.Revoked);
    }

    [Fact]
    public async Task Logout_ShouldKeepTheSessionOnAnotherDevice_WhenTheUserSignsOutOnOne()
    {
        // Arrange: signing out ends the session of this device only (owner decision on #931)
        const string password = "TestPass1!";
        string email = await RegisterUserAsync(password);
        WebsiteSession thisDevice = await SignInThroughTheWebsiteAsync(email, password);
        WebsiteSession otherDevice = await SignInThroughTheWebsiteAsync(email, password);

        // Act
        using HttpResponseMessage response = await SignOutAsync(thisDevice.IdToken, thisDevice.AuthCookies);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await OpenIddictTokenState.StatusOfAsync(Factory.Services, thisDevice.RefreshToken))
            .ShouldBe(OpenIddictConstants.Statuses.Revoked);
        (await OpenIddictTokenState.StatusOfAsync(Factory.Services, otherDevice.RefreshToken))
            .ShouldBe(OpenIddictConstants.Statuses.Valid);
        List<string?> authorizationStatuses =
            await OpenIddictTokenState.AuthorizationStatusesOfAsync(Factory.Services, thisDevice.UserId);
        authorizationStatuses.ShouldBe(
            [OpenIddictConstants.Statuses.Revoked, OpenIddictConstants.Statuses.Valid],
            ignoreOrder: true);
    }

    [Fact]
    public async Task Logout_ShouldRevokeTheSession_WhenTheHintIsTheIdTokenOfARefresh()
    {
        // Arrange: after its first refresh the website holds, and sends, the refreshed ID token
        WebsiteSession session = await SignInThroughTheWebsiteAsync();
        using HttpResponseMessage refreshResponse = await RefreshAsync(session.RefreshToken);
        refreshResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        using JsonDocument tokens = JsonDocument.Parse(await refreshResponse.Content.ReadAsStringAsync());
        string refreshedIdToken = tokens.RootElement.GetProperty("id_token").GetString()!;
        string refreshedRefreshToken = tokens.RootElement.GetProperty("refresh_token").GetString()!;

        // Act
        using HttpResponseMessage response = await SignOutAsync(refreshedIdToken, authCookies: []);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await OpenIddictTokenState.StatusOfAsync(Factory.Services, refreshedRefreshToken))
            .ShouldBe(OpenIddictConstants.Statuses.Revoked);
    }

    [Fact]
    public async Task Logout_ShouldRevokeNothing_WhenOnlyTheCookieIsSent()
    {
        // Arrange: the cookie belongs to the browser, not to one website session, so it cannot say which
        // session to end
        WebsiteSession session = await SignInThroughTheWebsiteAsync();

        // Act
        using HttpResponseMessage response = await SignOutAsync(idTokenHint: null, session.AuthCookies);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await OpenIddictTokenState.StatusOfAsync(Factory.Services, session.RefreshToken))
            .ShouldBe(OpenIddictConstants.Statuses.Valid);
    }

    [Fact]
    public async Task Logout_ShouldClearTheSignInServersCookie_WhenTheHintAndTheCookieAreSent()
    {
        // Arrange
        WebsiteSession session = await SignInThroughTheWebsiteAsync();

        // Act
        using HttpResponseMessage response = await SignOutAsync(session.IdToken, session.AuthCookies);

        // Assert
        string authCookieName = Factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme)
            .Cookie.Name!;
        response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? setCookies).ShouldBeTrue();
        setCookies.ShouldContain(cookie =>
            cookie.StartsWith(authCookieName + "=;", StringComparison.Ordinal)
            && cookie.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Logout_ShouldRevokeNothing_WhenTheIdTokenHintWasEditedToNameAnotherUser()
    {
        // Arrange: a caller edits the subject of their own hint to name someone else
        WebsiteSession caller = await SignInThroughTheWebsiteAsync();
        WebsiteSession victim = await SignInThroughTheWebsiteAsync();
        string editedHint = ReplaceInPayload(caller.IdToken, caller.UserId, victim.UserId);

        // Act
        using HttpResponseMessage response = await SignOutAsync(editedHint, authCookies: []);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await OpenIddictTokenState.StatusOfAsync(Factory.Services, victim.RefreshToken))
            .ShouldBe(OpenIddictConstants.Statuses.Valid);
        (await OpenIddictTokenState.StatusOfAsync(Factory.Services, caller.RefreshToken))
            .ShouldBe(OpenIddictConstants.Statuses.Valid);
    }

    [Fact]
    public async Task Logout_ShouldNotEndANewSession_WhenTheHintOfAnEndedSessionIsSentAgain()
    {
        // Arrange: anyone who holds an old hint can send this GET again
        (WebsiteSession endedSession, WebsiteSession newSession) = await SignInAgainAfterAnEndedSessionAsync();

        // Act
        using HttpResponseMessage response = await SignOutAsync(endedSession.IdToken, authCookies: []);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await OpenIddictTokenState.StatusOfAsync(Factory.Services, newSession.RefreshToken))
            .ShouldBe(OpenIddictConstants.Statuses.Valid);
    }

    [Fact]
    public async Task Discovery_ShouldAdvertiseTheRevocationEndpoint_WhenAnyClientAsks()
    {
        // Arrange: the website finds the endpoint here, never by a path of its own (#964)

        // Act
        using HttpResponseMessage response = await ApiClient.Http.GetAsync(
            new Uri(".well-known/openid-configuration", UriKind.Relative));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using JsonDocument discovery = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Uri revocationEndpoint = new(discovery.RootElement.GetProperty("revocation_endpoint").GetString()!);
        revocationEndpoint.AbsolutePath.ShouldBe("/connect/revoke");
    }

    [Fact]
    public async Task Revoke_ShouldEndTheWebsitesRefreshToken_WhenTheWebsiteRevokesIt()
    {
        // Arrange: the website's own revoke before it hands the sign-out to the browser (#964)
        WebsiteSession session = await SignInThroughTheWebsiteAsync();

        // Act
        using HttpResponseMessage revokeResponse = await RevokeAsync(session.RefreshToken);

        // Assert
        revokeResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await OpenIddictTokenState.StatusOfAsync(Factory.Services, session.RefreshToken))
            .ShouldBe(OpenIddictConstants.Statuses.Revoked);
        using HttpResponseMessage refreshResponse = await RefreshAsync(session.RefreshToken);
        refreshResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Revoke_ShouldKeepTheSessionOnAnotherDevice_WhenTheWebsiteRevokesTheRefreshTokenOfOne()
    {
        // Arrange
        const string password = "TestPass1!";
        string email = await RegisterUserAsync(password);
        WebsiteSession thisDevice = await SignInThroughTheWebsiteAsync(email, password);
        WebsiteSession otherDevice = await SignInThroughTheWebsiteAsync(email, password);

        // Act
        using HttpResponseMessage revokeResponse = await RevokeAsync(thisDevice.RefreshToken);

        // Assert
        revokeResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await OpenIddictTokenState.StatusOfAsync(Factory.Services, otherDevice.RefreshToken))
            .ShouldBe(OpenIddictConstants.Statuses.Valid);
    }

    [Fact]
    public async Task Logout_ShouldStillRevokeTheSession_WhenTheWebsiteRevokedItsRefreshTokenFirst()
    {
        // Arrange: the browser still goes to the end-session page after the website's own revoke
        WebsiteSession session = await SignInThroughTheWebsiteAsync();
        using HttpResponseMessage revokeResponse = await RevokeAsync(session.RefreshToken);
        revokeResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        List<string?> statusesAfterTheRevoke =
            await OpenIddictTokenState.AuthorizationStatusesOfAsync(Factory.Services, session.UserId);
        statusesAfterTheRevoke.ShouldHaveSingleItem().ShouldBe(OpenIddictConstants.Statuses.Valid);

        // Act
        using HttpResponseMessage response = await SignOutAsync(session.IdToken, authCookies: []);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location.ShouldNotBeNull().ToString().ShouldStartWith(PostLogoutRedirectUri);
        List<string?> authorizationStatuses =
            await OpenIddictTokenState.AuthorizationStatusesOfAsync(Factory.Services, session.UserId);
        authorizationStatuses.ShouldHaveSingleItem().ShouldBe(OpenIddictConstants.Statuses.Revoked);
    }

    [Fact]
    public async Task Revoke_ShouldAnswerOk_WhenTheRefreshTokenWasAlreadyRevoked()
    {
        // Arrange: a sign-out after the session already ended elsewhere (RFC 7009 §2.2)
        WebsiteSession session = await SignInThroughTheWebsiteAsync();
        using HttpResponseMessage firstResponse = await RevokeAsync(session.RefreshToken);
        firstResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Act
        using HttpResponseMessage response = await RevokeAsync(session.RefreshToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(WrongClientSecret)]
    public async Task AuthorizationCodeExchange_ShouldRefuseTheWebsite_WhenItsSecretIsMissingOrWrong(string? clientSecret)
    {
        // Arrange
        (string authorizationCode, string codeVerifier, _, _) = await ObtainAuthorizationCodeAsync();

        // Act
        using HttpResponseMessage response = await ExchangeAuthorizationCodeAsync(
            ApiClient.Http, authorizationCode, codeVerifier, WebsiteClientId, clientSecret);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReadErrorAsync(response)).ShouldBe(OpenIddictConstants.Errors.InvalidClient);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(WrongClientSecret)]
    public async Task RefreshTokenGrant_ShouldRefuseTheWebsite_WhenItsSecretIsMissingOrWrong(string? clientSecret)
    {
        // Arrange
        WebsiteSession session = await SignInThroughTheWebsiteAsync();

        // Act
        using HttpResponseMessage response = await RefreshAsync(session.RefreshToken, WebsiteClientId, clientSecret);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReadErrorAsync(response)).ShouldBe(OpenIddictConstants.Errors.InvalidClient);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(WrongClientSecret)]
    public async Task Revoke_ShouldRefuseTheWebsiteAndKeepTheToken_WhenItsSecretIsMissingOrWrong(string? clientSecret)
    {
        // Arrange
        WebsiteSession session = await SignInThroughTheWebsiteAsync();

        // Act
        using HttpResponseMessage response = await RevokeAsync(session.RefreshToken, WebsiteClientId, clientSecret);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReadErrorAsync(response)).ShouldBe(OpenIddictConstants.Errors.InvalidClient);
        (await OpenIddictTokenState.StatusOfAsync(Factory.Services, session.RefreshToken))
            .ShouldBe(OpenIddictConstants.Statuses.Valid);
    }

    /// <summary>
    /// Otherwise the old public client would be a way around the secret for the website's own codes.
    /// </summary>
    [Fact]
    public async Task AuthorizationCodeExchange_ShouldRefuseTheWebsitesCode_WhenTheOldPublicClientPresentsIt()
    {
        // Arrange
        (string authorizationCode, string codeVerifier, _, _) = await ObtainAuthorizationCodeAsync();

        // Act
        using HttpResponseMessage response = await ExchangeAuthorizationCodeAsync(
            ApiClient.Http, authorizationCode, codeVerifier, OldPublicClientId, clientSecret: null);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(response)).ShouldBe(OpenIddictConstants.Errors.InvalidGrant);
    }

    [Fact]
    public async Task RefreshTokenGrant_ShouldRefuseTheWebsitesToken_WhenTheOldPublicClientPresentsIt()
    {
        // Arrange
        WebsiteSession session = await SignInThroughTheWebsiteAsync();

        // Act
        using HttpResponseMessage response = await RefreshAsync(session.RefreshToken, OldPublicClientId, clientSecret: null);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(response)).ShouldBe(OpenIddictConstants.Errors.InvalidGrant);
    }

    /// <summary>
    /// The token endpoint gives a service token to any client OpenIddict lets through, so the missing
    /// permission is the only gate now that the website can authenticate.
    /// </summary>
    [Fact]
    public async Task ClientCredentialsGrant_ShouldRefuseTheWebsite_EvenWithItsSecret()
    {
        // Arrange
        using FormUrlEncodedContent request = new(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = WebsiteClientId,
            ["client_secret"] = WebsiteClientSecret,
            ["scope"] = "api"
        });

        // Act
        using HttpResponseMessage response = await ApiClient.Http.PostAsync(
            new Uri("connect/token", UriKind.Relative), request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(response)).ShouldBe(OpenIddictConstants.Errors.UnauthorizedClient);
    }

    /// <summary>
    /// A website of the previous release keeps signing in while this release runs (ADR-0074).
    /// </summary>
    [Fact]
    public async Task OldPublicClient_ShouldStillSignInAndRefresh_WithoutASecret()
    {
        // Arrange
        const string password = "TestPass1!";
        string email = await RegisterUserAsync(password);
        (string authorizationCode, string codeVerifier, _) =
            await ObtainAuthorizationCodeForAsync(Factory, email, password, OldPublicClientId);

        // Act
        using HttpResponseMessage exchangeResponse = await ExchangeAuthorizationCodeAsync(
            ApiClient.Http, authorizationCode, codeVerifier, OldPublicClientId, clientSecret: null);
        exchangeResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        using JsonDocument tokens = JsonDocument.Parse(await exchangeResponse.Content.ReadAsStringAsync());
        string refreshToken = tokens.RootElement.GetProperty("refresh_token").GetString()!;
        using HttpResponseMessage refreshResponse = await RefreshAsync(refreshToken, OldPublicClientId, clientSecret: null);

        // Assert
        refreshResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// Why the website needs a new client id: one registration cannot take a secret from one website and
    /// none from another (ADR-0074).
    /// </summary>
    [Fact]
    public async Task AuthorizationCodeExchange_ShouldRefuseTheOldPublicClient_WhenItSendsASecret()
    {
        // Arrange
        const string password = "TestPass1!";
        string email = await RegisterUserAsync(password);
        (string authorizationCode, string codeVerifier, _) =
            await ObtainAuthorizationCodeForAsync(Factory, email, password, OldPublicClientId);

        // Act
        using HttpResponseMessage response = await ExchangeAuthorizationCodeAsync(
            ApiClient.Http, authorizationCode, codeVerifier, OldPublicClientId, WebsiteClientSecret);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReadErrorAsync(response)).ShouldBe(OpenIddictConstants.Errors.InvalidClient);
    }

    /// <summary>
    /// Every log line at every level, OpenIddict's own request logs included, across the website's three
    /// calls and a refused one.
    /// </summary>
    [Fact]
    public async Task WebsiteCalls_ShouldNeverWriteTheSecretToALogLine()
    {
        // Arrange
        using CapturingLoggerFactory loggerFactory = new();
        using WebApplicationFactory<Program> host = Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                // A second relay on this database could take a row another test waits for.
                AuthSystemApiFactory.RemoveHostedService<OutboxRelay>(services);
                services.AddSingleton<ILoggerFactory>(loggerFactory);
            }));
        using HttpClient client = host.CreateClient();
        const string password = "TestPass1!";
        string email = await RegisterUserAsync(password);

        // Act
        (string authorizationCode, string codeVerifier, _) =
            await ObtainAuthorizationCodeForAsync(host, email, password, WebsiteClientId);
        using HttpResponseMessage exchangeResponse = await ExchangeAuthorizationCodeAsync(
            client, authorizationCode, codeVerifier, WebsiteClientId, WebsiteClientSecret);
        exchangeResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        using JsonDocument tokens = JsonDocument.Parse(await exchangeResponse.Content.ReadAsStringAsync());
        string refreshToken = tokens.RootElement.GetProperty("refresh_token").GetString()!;
        using HttpResponseMessage refused = await PostClientRequestAsync(
            client,
            "connect/token",
            new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken },
            WebsiteClientId,
            WebsiteClientSecret + "x");
        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using HttpResponseMessage refreshResponse = await PostClientRequestAsync(
            client,
            "connect/token",
            new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken },
            WebsiteClientId,
            WebsiteClientSecret);
        refreshResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        using JsonDocument renewed = JsonDocument.Parse(await refreshResponse.Content.ReadAsStringAsync());
        using HttpResponseMessage revokeResponse = await PostClientRequestAsync(
            client,
            "connect/revoke",
            new Dictionary<string, string>
            {
                ["token"] = renewed.RootElement.GetProperty("refresh_token").GetString()!,
                ["token_type_hint"] = "refresh_token"
            },
            WebsiteClientId,
            WebsiteClientSecret);
        revokeResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Assert: OpenIddict logs each request's parameters, so the client id shows the check can see them
        loggerFactory.Entries.ShouldContain(entry => entry.Message.Contains(WebsiteClientId, StringComparison.Ordinal));
        string[] secretForms = [WebsiteClientSecret, Uri.EscapeDataString(WebsiteClientSecret)];
        loggerFactory.Entries.ShouldAllBe(entry => secretForms.All(secret =>
            !entry.Message.Contains(secret, StringComparison.Ordinal)
            && (entry.Exception == null || !entry.Exception.ToString().Contains(secret, StringComparison.Ordinal))));
    }

    private async Task<(string Code, string CodeVerifier, List<string> AuthCookies, string Email)>
        ObtainAuthorizationCodeAsync(string? password = null)
    {
        password ??= "TestPass1!";
        string email = await RegisterUserAsync(password);

        (string authorizationCode, string codeVerifier, List<string> authCookies) =
            await ObtainAuthorizationCodeForAsync(email, password);

        return (authorizationCode, codeVerifier, authCookies, email);
    }

    private Task<(string Code, string CodeVerifier, List<string> AuthCookies)>
        ObtainAuthorizationCodeForAsync(string email, string password) =>
        ObtainAuthorizationCodeForAsync(Factory, email, password);

    private static async Task<(string Code, string CodeVerifier, List<string> AuthCookies)>
        ObtainAuthorizationCodeForAsync(
            WebApplicationFactory<Program> host,
            string email,
            string password,
            string clientId = WebsiteClientId)
    {
        // A client of its own, like a browser on another device, so two sign-ins in one test never share
        // the antiforgery cookie that the login page hands out only once per browser
        using HttpClient browser = host.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });

        (string codeVerifier, string codeChallenge) = GeneratePkce();
        string authorizeUrl = BuildAuthorizeUrl(codeChallenge, clientId);

        // Step 1: Hit /connect/authorize - should redirect to login
        HttpResponseMessage authorizeResponse = await browser.GetAsync(
            new Uri(authorizeUrl, UriKind.Relative));
        string loginRedirect = authorizeResponse.Headers.Location!.ToString();

        // Step 2: GET the login page
        HttpResponseMessage loginPageResponse = await browser.GetAsync(
            new Uri(loginRedirect, UriKind.RelativeOrAbsolute));
        string loginPageHtml = await loginPageResponse.Content.ReadAsStringAsync();

        string? antiForgeryToken = ExtractAntiForgeryToken(loginPageHtml);
        IEnumerable<string> setCookieHeaders = loginPageResponse.Headers.GetValues("Set-Cookie");

        // Step 3: POST login credentials
        string? returnUrl = ExtractReturnUrl(loginRedirect);

        Dictionary<string, string> loginFormData = new()
        {
            ["Email"] = email,
            ["Password"] = password,
        };

        if (antiForgeryToken is not null)
        {
            loginFormData["__RequestVerificationToken"] = antiForgeryToken;
        }

        using FormUrlEncodedContent loginContent = new(loginFormData);

        string loginPostUrl = returnUrl is not null
            ? $"/Account/Login?returnUrl={Uri.EscapeDataString(returnUrl)}"
            : "/Account/Login";

        using HttpRequestMessage loginPostRequest = new(HttpMethod.Post, loginPostUrl);
        loginPostRequest.Content = loginContent;

        foreach (string cookie in setCookieHeaders)
        {
            string cookieName = cookie.Split('=')[0];
            string cookieValue = cookie.Split('=')[1].Split(';')[0];
            loginPostRequest.Headers.Add("Cookie", $"{cookieName}={cookieValue}");
        }

        HttpResponseMessage loginResponse = await browser.SendAsync(loginPostRequest);
        string postLoginRedirect = loginResponse.Headers.Location!.ToString();

        List<string> authCookies = loginResponse.Headers
            .Where(h => h.Key == "Set-Cookie")
            .SelectMany(h => h.Value)
            .ToList();

        // Step 4: Follow redirect back to /connect/authorize (now with auth cookie)
        using HttpRequestMessage authorizeWithCookieRequest = new(HttpMethod.Get, postLoginRedirect);
        foreach (string cookie in authCookies)
        {
            string cookiePair = cookie.Split(';')[0];
            authorizeWithCookieRequest.Headers.Add("Cookie", cookiePair);
        }

        HttpResponseMessage authorizeWithCookieResponse =
            await browser.SendAsync(authorizeWithCookieRequest);

        string callbackUrl = authorizeWithCookieResponse.Headers.Location!.ToString();
        Uri callbackUri = new(callbackUrl);
        NameValueCollection queryParams = HttpUtility.ParseQueryString(callbackUri.Query);
        string authorizationCode = queryParams["code"]!;

        return (authorizationCode, codeVerifier, authCookies);
    }

    private async Task<string> RegisterUserAsync(string password)
    {
        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, password);

        return registerRequest.Email;
    }

    private async Task<WebsiteSession> SignInThroughTheWebsiteAsync()
    {
        const string password = "TestPass1!";
        string email = await RegisterUserAsync(password);

        return await SignInThroughTheWebsiteAsync(email, password);
    }

    private async Task<WebsiteSession> SignInThroughTheWebsiteAsync(string email, string password)
    {
        (string authorizationCode, string codeVerifier, List<string> authCookies) =
            await ObtainAuthorizationCodeForAsync(email, password);

        using HttpResponseMessage tokenResponse = await ExchangeAuthorizationCodeAsync(authorizationCode, codeVerifier);
        tokenResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        using JsonDocument tokens = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
        string idToken = tokens.RootElement.GetProperty("id_token").GetString()!;
        using JsonDocument identity = JsonDocument.Parse(JwtPayload.Read(idToken));

        return new WebsiteSession(
            identity.RootElement.GetProperty("sub").GetString()!,
            idToken,
            tokens.RootElement.GetProperty("refresh_token").GetString()!,
            authCookies);
    }

    /// <summary>
    /// Signs a user in, signs out with the hint alone, and signs the same user in again as a new device.
    /// </summary>
    private async Task<(WebsiteSession Ended, WebsiteSession New)> SignInAgainAfterAnEndedSessionAsync()
    {
        const string password = "TestPass1!";
        string email = await RegisterUserAsync(password);

        WebsiteSession endedSession = await SignInThroughTheWebsiteAsync(email, password);
        using (HttpResponseMessage logoutResponse = await SignOutAsync(endedSession.IdToken, authCookies: []))
        {
            logoutResponse.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        }

        WebsiteSession newSession = await SignInThroughTheWebsiteAsync(email, password);
        return (endedSession, newSession);
    }

    /// <summary>
    /// Sends only the given cookies, never the ones an earlier response set.
    /// </summary>
    private async Task<HttpResponseMessage> SignOutAsync(string? idTokenHint, List<string> authCookies)
    {
        string logoutUrl = $"connect/logout?post_logout_redirect_uri={Uri.EscapeDataString(PostLogoutRedirectUri)}";
        if (idTokenHint is not null)
        {
            logoutUrl += $"&id_token_hint={Uri.EscapeDataString(idTokenHint)}";
        }

        using HttpClient client = Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(logoutUrl, UriKind.Relative));
        foreach (string cookie in authCookies)
        {
            request.Headers.Add("Cookie", cookie.Split(';')[0]);
        }

        return await client.SendAsync(request);
    }

    private Task<HttpResponseMessage> RefreshAsync(string refreshToken) =>
        RefreshAsync(refreshToken, WebsiteClientId, WebsiteClientSecret);

    private Task<HttpResponseMessage> RefreshAsync(string refreshToken, string clientId, string? clientSecret) =>
        PostClientRequestAsync(
            ApiClient.Http,
            "connect/token",
            new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken },
            clientId,
            clientSecret);

    /// <summary>
    /// The request the website sends: its client id and its secret.
    /// </summary>
    private Task<HttpResponseMessage> RevokeAsync(string refreshToken) =>
        RevokeAsync(refreshToken, WebsiteClientId, WebsiteClientSecret);

    private Task<HttpResponseMessage> RevokeAsync(string refreshToken, string clientId, string? clientSecret) =>
        PostClientRequestAsync(
            ApiClient.Http,
            "connect/revoke",
            new Dictionary<string, string> { ["token"] = refreshToken, ["token_type_hint"] = "refresh_token" },
            clientId,
            clientSecret);

    /// <summary>
    /// A null secret sends no <c>client_secret</c> field, as a public client does.
    /// </summary>
    private static async Task<HttpResponseMessage> PostClientRequestAsync(
        HttpClient client,
        string path,
        Dictionary<string, string> fields,
        string clientId,
        string? clientSecret)
    {
        fields["client_id"] = clientId;
        if (clientSecret is not null)
        {
            fields["client_secret"] = clientSecret;
        }

        using FormUrlEncodedContent content = new(fields);
        return await client.PostAsync(new Uri(path, UriKind.Relative), content);
    }

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response)
    {
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("error").GetString();
    }

    /// <summary>
    /// Changes the payload and keeps the original signature, so the result no longer passes the check.
    /// </summary>
    private static string ReplaceInPayload(string jwt, string oldValue, string newValue)
    {
        string[] segments = jwt.Split('.');
        string payload = JwtPayload.Read(jwt).Replace(oldValue, newValue, StringComparison.Ordinal);

        return $"{segments[0]}.{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload))}.{segments[2]}";
    }

    private Task<HttpResponseMessage> ExchangeAuthorizationCodeAsync(string authorizationCode, string codeVerifier) =>
        ExchangeAuthorizationCodeAsync(ApiClient.Http, authorizationCode, codeVerifier);

    private static Task<HttpResponseMessage> ExchangeAuthorizationCodeAsync(
        HttpClient client,
        string authorizationCode,
        string codeVerifier) =>
        ExchangeAuthorizationCodeAsync(client, authorizationCode, codeVerifier, WebsiteClientId, WebsiteClientSecret);

    private static Task<HttpResponseMessage> ExchangeAuthorizationCodeAsync(
        HttpClient client,
        string authorizationCode,
        string codeVerifier,
        string clientId,
        string? clientSecret) =>
        PostClientRequestAsync(
            client,
            "connect/token",
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = authorizationCode,
                ["redirect_uri"] = RedirectUri,
                ["code_verifier"] = codeVerifier
            },
            clientId,
            clientSecret);

    /// <summary>
    /// Issues codes that name the translation API alone, as before #1023. The code exchange, which is the
    /// code under test, is left as it is.
    /// </summary>
    private WebApplicationFactory<Program> CreateHostThatIssuesCodesWithoutTheAuthApi() =>
        Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                // A second relay on this database could take a row another test waits for.
                AuthSystemApiFactory.RemoveHostedService<OutboxRelay>(services);

                services.OverrideSignInAudiences(
                    context => context.EndpointType is OpenIddictServerEndpointType.Authorization,
                    AuthConstants.ClientIds.Api);
            }));

    private static string BuildAuthorizeUrl(string codeChallenge, string clientId = WebsiteClientId) =>
        $"connect/authorize?response_type=code" +
        $"&client_id={clientId}" +
        $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
        $"&scope={Uri.EscapeDataString("openid email profile roles api offline_access")}" +
        $"&code_challenge={codeChallenge}" +
        $"&code_challenge_method=S256";

    private static (string codeVerifier, string codeChallenge) GeneratePkce()
    {
        byte[] randomBytes = new byte[32];
        using RandomNumberGenerator rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomBytes);
        string codeVerifier = Convert.ToBase64String(randomBytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        byte[] challengeBytes = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        string codeChallenge = Convert.ToBase64String(challengeBytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return (codeVerifier, codeChallenge);
    }

    private static string? ExtractAntiForgeryToken(string html)
    {
        Match match = AntiForgeryTokenRegex().Match(html);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ExtractReturnUrl(string loginRedirectUrl)
    {
        int queryStart = loginRedirectUrl.IndexOf('?', StringComparison.Ordinal);
        if (queryStart < 0)
        {
            return null;
        }

        NameValueCollection queryParams = HttpUtility.ParseQueryString(loginRedirectUrl[queryStart..]);
        return queryParams["ReturnUrl"] ?? queryParams["returnUrl"];
    }

    [GeneratedRegex("""name="__RequestVerificationToken".*?value="([^"]+)""")]
    private static partial Regex AntiForgeryTokenRegex();

    private sealed record WebsiteSession(string UserId, string IdToken, string RefreshToken, List<string> AuthCookies);
}
