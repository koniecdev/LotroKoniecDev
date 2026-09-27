using System.Collections.Specialized;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using Microsoft.EntityFrameworkCore;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

[Collection("AuthApi")]
public sealed partial class AuthorizationCodeFlowTests : AsyncLifetimeTestBase
{
    protected override TestApiClient ApiClient { get; }

    private readonly HttpClient _noRedirectClient;

    public AuthorizationCodeFlowTests(AuthSystemApiFactory appFactory) : base(appFactory)
    {
        JsonSerializerOptions jsonSerializerOptions =
            appFactory.Services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;

        // Client that follows redirects (for normal API calls and registration)
        ApiClient = new TestApiClient(appFactory.CreateClient(), jsonSerializerOptions);

        // Client that does NOT follow redirects (for testing auth code flow redirects)
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
    /// Sign-in starts here, and the browser reaches this endpoint directly, not through an account page.
    /// When the database is down, the browser still gets the Polish error page and not the JSON (#867).
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
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/html");
        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("<h1>Coś poszło nie tak</h1>");
    }

    [Fact]
    public async Task FullAuthorizationCodeFlow_ShouldReturnTokens()
    {
        // Arrange: Register a user
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

        // Extract anti-forgery token and cookies from the login page
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

        // Build login POST URL with returnUrl
        string loginPostUrl = returnUrl is not null
            ? $"/Account/Login?returnUrl={Uri.EscapeDataString(returnUrl)}"
            : "/Account/Login";

        using HttpRequestMessage loginPostRequest = new(HttpMethod.Post, loginPostUrl);
        loginPostRequest.Content = loginContent;

        // Copy cookies from login page response
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

        // Capture the auth cookie
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

        // Extract authorization code
        Uri callbackUri = new(callbackUrl);
        NameValueCollection queryParams = HttpUtility.ParseQueryString(callbackUri.Query);
        string authorizationCode = queryParams["code"]!;
        authorizationCode.ShouldNotBeNullOrEmpty();

        // Step 5: Exchange authorization code for tokens
        using FormUrlEncodedContent tokenRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = authorizationCode,
            ["redirect_uri"] = "https://localhost:5001/callback",
            ["client_id"] = "lotrokoniecdev-web",
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
        // Arrange - First get the login page to extract anti-forgery token and cookies
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

        // Copy cookies from login page response
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
        // Arrange: a registered but UNCONFIRMED user, logging in with the CORRECT credentials.
        // RequireConfirmedEmail is enabled, so the interactive login must reject this user.
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

        // Assert: login is rejected: the page is re-rendered (200) with the unconfirmed-account
        // error (ADR-0046), NOT a redirect (302), which is what a successful sign-in would return.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("To konto nie zostało jeszcze aktywowane");
    }

    [Fact]
    public async Task AuthorizationCodeExchange_ShouldFail_WhenCodeVerifierIsInvalid()
    {
        // Arrange: Get a valid authorization code through the full auth flow
        (string authorizationCode, _, _, _) = await ObtainAuthorizationCodeAsync();

        // Use a completely different code verifier that doesn't match the original challenge
        string wrongCodeVerifier = "this-is-a-wrong-verifier-that-does-not-match-the-challenge";

        using FormUrlEncodedContent tokenRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = authorizationCode,
            ["redirect_uri"] = "https://localhost:5001/callback",
            ["client_id"] = "lotrokoniecdev-web",
            ["code_verifier"] = wrongCodeVerifier
        });

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsync(
            new Uri("connect/token", UriKind.Relative), tokenRequest);

        // Assert: Invalid PKCE verifier should be rejected
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AuthorizationCodeExchange_ShouldFail_WhenAuthorizationCodeIsExpired()
    {
        // Arrange: Get a valid authorization code
        (string authorizationCode, string codeVerifier, _, _) = await ObtainAuthorizationCodeAsync();

        // Expire the authorization code in the database
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await dbContext.Database.ExecuteSqlRawAsync(
            """UPDATE authsystem."OpenIddictTokens" SET "ExpirationDate" = '2020-01-01 00:00:00+00' WHERE "Type" = 'urn:openiddict:params:oauth:token-type:authorization_code'""");

        using FormUrlEncodedContent tokenRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = authorizationCode,
            ["redirect_uri"] = "https://localhost:5001/callback",
            ["client_id"] = "lotrokoniecdev-web",
            ["code_verifier"] = codeVerifier
        });

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsync(
            new Uri("connect/token", UriKind.Relative), tokenRequest);

        // Assert: Expired authorization code should be rejected
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AuthorizationCodeExchange_ShouldFail_WhenSecurityStampChangedAfterAuthorize()
    {
        // Arrange: a password reset lands between /connect/authorize and the code exchange (#848)
        (string authorizationCode, string codeVerifier, _, string email) = await ObtainAuthorizationCodeAsync();

        await using (AsyncServiceScope scope = Factory.Services.CreateAsyncScope())
        {
            UserManager<ApplicationUser> userManager =
                scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            ApplicationUser? user = await userManager.FindByEmailAsync(email);
            user.ShouldNotBeNull();
            (await userManager.UpdateSecurityStampAsync(user)).Succeeded.ShouldBeTrue();
        }

        // Act
        using HttpResponseMessage response = await ExchangeAuthorizationCodeAsync(authorizationCode, codeVerifier);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("invalid_grant");
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
        (await response.Content.ReadAsStringAsync()).ShouldContain("invalid_grant");
    }

    [Fact]
    public async Task AuthorizationCodeExchange_ShouldFail_WhenTheAccountWasDeletedAfterAuthorize()
    {
        // Arrange: an operator deletes the row by hand, as the runbook's admin fixes do, while a code is
        // still in flight
        (string authorizationCode, string codeVerifier, _, string email) = await ObtainAuthorizationCodeAsync();

        await using (AsyncServiceScope scope = Factory.Services.CreateAsyncScope())
        {
            UserManager<ApplicationUser> userManager =
                scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            ApplicationUser? user = await userManager.FindByEmailAsync(email);
            user.ShouldNotBeNull();
            (await userManager.DeleteAsync(user)).Succeeded.ShouldBeTrue();
        }

        // Act
        using HttpResponseMessage response = await ExchangeAuthorizationCodeAsync(authorizationCode, codeVerifier);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("invalid_grant");
    }

    [Fact]
    public async Task AuthorizationCodeFlow_ShouldIssueARefreshTokenThatRefreshes()
    {
        // Arrange: this is the frontend's sign-in. Its refresh token must carry the stamp, or every
        // browser session would end at its first refresh (#848).
        (string authorizationCode, string codeVerifier, _, _) = await ObtainAuthorizationCodeAsync();

        using HttpResponseMessage tokenResponse = await ExchangeAuthorizationCodeAsync(authorizationCode, codeVerifier);
        tokenResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        string tokenContent = await tokenResponse.Content.ReadAsStringAsync();
        string refreshToken;
        using (JsonDocument tokenJson = JsonDocument.Parse(tokenContent))
        {
            refreshToken = tokenJson.RootElement.GetProperty("refresh_token").GetString()!;
        }

        using FormUrlEncodedContent refreshRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = "lotrokoniecdev-web"
        });

        // Act
        using HttpResponseMessage response = await ApiClient.Http.PostAsync(
            new Uri("connect/token", UriKind.Relative), refreshRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
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
    public async Task Logout_ShouldRedirectToPostLogoutRedirectUri_WhenIdTokenHintIsProvided()
    {
        // Arrange: Complete the full auth code flow to get an id_token and auth cookies
        (string authorizationCode, string codeVerifier, List<string> authCookies, _) =
            await ObtainAuthorizationCodeAsync();

        // Exchange the authorization code for tokens (including id_token)
        using FormUrlEncodedContent tokenRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = authorizationCode,
            ["redirect_uri"] = "https://localhost:5001/callback",
            ["client_id"] = "lotrokoniecdev-web",
            ["code_verifier"] = codeVerifier
        });

        HttpResponseMessage tokenResponse = await ApiClient.Http.PostAsync(
            new Uri("connect/token", UriKind.Relative), tokenRequest);
        tokenResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        string tokenContent = await tokenResponse.Content.ReadAsStringAsync();
        using JsonDocument tokenJson = JsonDocument.Parse(tokenContent);
        string idToken = tokenJson.RootElement.GetProperty("id_token").GetString()!;

        // Build the logout URL with id_token_hint and post_logout_redirect_uri
        const string postLogoutRedirectUri = "https://localhost:5001";
        string logoutUrl = $"connect/logout?id_token_hint={Uri.EscapeDataString(idToken)}" +
            $"&post_logout_redirect_uri={Uri.EscapeDataString(postLogoutRedirectUri)}";

        using HttpRequestMessage logoutRequest = new(HttpMethod.Get, logoutUrl);
        foreach (string cookie in authCookies)
        {
            string cookiePair = cookie.Split(';')[0];
            logoutRequest.Headers.Add("Cookie", cookiePair);
        }

        // Act
        HttpResponseMessage logoutResponse = await _noRedirectClient.SendAsync(logoutRequest);

        // Assert: Should redirect to the post_logout_redirect_uri
        logoutResponse.StatusCode.ShouldBe(HttpStatusCode.Redirect);

        string? location = logoutResponse.Headers.Location?.ToString();
        location.ShouldNotBeNull();
        location.ShouldStartWith(postLogoutRedirectUri);
    }

    private async Task<(string Code, string CodeVerifier, List<string> AuthCookies, string Email)>
        ObtainAuthorizationCodeAsync(string? password = null)
    {
        password ??= "TestPass1!";
        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, password);

        (string codeVerifier, string codeChallenge) = GeneratePkce();
        string authorizeUrl = BuildAuthorizeUrl(codeChallenge);

        // Step 1: Hit /connect/authorize - should redirect to login
        HttpResponseMessage authorizeResponse = await _noRedirectClient.GetAsync(
            new Uri(authorizeUrl, UriKind.Relative));
        string loginRedirect = authorizeResponse.Headers.Location!.ToString();

        // Step 2: GET the login page
        HttpResponseMessage loginPageResponse = await _noRedirectClient.GetAsync(
            new Uri(loginRedirect, UriKind.RelativeOrAbsolute));
        string loginPageHtml = await loginPageResponse.Content.ReadAsStringAsync();

        // Extract anti-forgery token and cookies from the login page
        string? antiForgeryToken = ExtractAntiForgeryToken(loginPageHtml);
        IEnumerable<string> setCookieHeaders = loginPageResponse.Headers.GetValues("Set-Cookie");

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
        string postLoginRedirect = loginResponse.Headers.Location!.ToString();

        // Capture the auth cookie
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
            await _noRedirectClient.SendAsync(authorizeWithCookieRequest);

        string callbackUrl = authorizeWithCookieResponse.Headers.Location!.ToString();
        Uri callbackUri = new(callbackUrl);
        NameValueCollection queryParams = HttpUtility.ParseQueryString(callbackUri.Query);
        string authorizationCode = queryParams["code"]!;

        return (authorizationCode, codeVerifier, authCookies, registerRequest.Email);
    }

    private async Task<HttpResponseMessage> ExchangeAuthorizationCodeAsync(string authorizationCode, string codeVerifier)
    {
        using FormUrlEncodedContent tokenRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = authorizationCode,
            ["redirect_uri"] = "https://localhost:5001/callback",
            ["client_id"] = "lotrokoniecdev-web",
            ["code_verifier"] = codeVerifier
        });

        return await ApiClient.Http.PostAsync(new Uri("connect/token", UriKind.Relative), tokenRequest);
    }

    private static string BuildAuthorizeUrl(string codeChallenge) =>
        $"connect/authorize?response_type=code" +
        $"&client_id=lotrokoniecdev-web" +
        $"&redirect_uri={Uri.EscapeDataString("https://localhost:5001/callback")}" +
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
}
