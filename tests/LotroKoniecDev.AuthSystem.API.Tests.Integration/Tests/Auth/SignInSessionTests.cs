using System.Collections.Specialized;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Options;
using NSubstitute;
using LotroKoniecDev.AuthSystem.API.BackgroundServices;
using LotroKoniecDev.AuthSystem.API.Services.Sessions;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Password;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.AuthSystem.Persistence.Sessions;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// The sign-in server's cookie carries only a session key, and the session lives in the auth database
/// (ADR-0062, #1013). Every device is its own client that keeps no cookies, so each request carries
/// exactly the cookie the test hands it. A stolen copy is the same cookie sent by someone else.
/// </summary>
public sealed partial class SignInSessionTests : EndpointsTestBase
{
    private const string Password = "TestPass1!";
    private const string NewPassword = "NewPass99!";
    private const string WebClientId = "lotrokoniecdev-web";
    private const string RedirectUri = AuthSystemApiFactory.TestFrontendAppRoot + "/callback";

    /// <summary>
    /// The precision a stored expiry keeps: the ticket writes its dates to the second.
    /// </summary>
    private static readonly TimeSpan ExpiryTolerance = TimeSpan.FromSeconds(2);

    public SignInSessionTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public async Task Authorize_ShouldSendACopyOfTheCookieToTheLoginForm_AfterTheOriginalSignedOut()
    {
        // Arrange: "Zapamiętaj mnie", so the cookie lasts 30 days, and a copy taken while it still works
        (string email, _) = await RegisterUserAsync();
        string originalCookie = await SignInAsync(email, rememberMe: true);
        string stolenCopy = originalCookie;
        string idToken = await SignInToTheWebsiteAsync(originalCookie);

        (HttpStatusCode copyStatusBefore, string copyLocationBefore) = await AuthorizeAsync(stolenCopy);
        copyStatusBefore.ShouldBe(HttpStatusCode.Redirect);
        copyLocationBefore.ShouldContain("code=");

        // Act: the website's sign-out, with its hint and the browser's own cookie
        using HttpResponseMessage signOutResponse = await SignOutAsync(idToken, originalCookie);

        // Assert
        signOutResponse.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (HttpStatusCode copyStatus, string copyLocation) = await AuthorizeAsync(stolenCopy);
        copyStatus.ShouldBe(HttpStatusCode.Redirect);
        copyLocation.ShouldContain("/Account/Login");
        copyLocation.ShouldNotContain("code=");
    }

    [Fact]
    public async Task Authorize_ShouldKeepTheOtherDevicesCookieWorking_WhenOneDeviceSignsOut()
    {
        // Arrange: signing out still ends this device only (#931)
        (string email, _) = await RegisterUserAsync();
        string thisDevice = await SignInAsync(email, rememberMe: true);
        string otherDevice = await SignInAsync(email, rememberMe: true);

        // Act
        using HttpResponseMessage signOutResponse = await SignOutAsync(idTokenHint: null, thisDevice);

        // Assert: the device that signed out lost its session, which shows the sign-out did something
        signOutResponse.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await AuthorizeAsync(thisDevice)).Location.ShouldContain("/Account/Login");
        (HttpStatusCode otherStatus, string otherLocation) = await AuthorizeAsync(otherDevice);
        otherStatus.ShouldBe(HttpStatusCode.Redirect);
        otherLocation.ShouldContain("code=");
    }

    [Fact]
    public async Task Authorize_ShouldSendEveryStoredSessionOfTheUserToTheLoginForm_AfterAPasswordChange()
    {
        // Arrange: one remembered and one short session, each on its own device
        (string email, Guid userId) = await RegisterUserAsync();
        string rememberedDevice = await SignInAsync(email, rememberMe: true);
        string shortSessionDevice = await SignInAsync(email, rememberMe: false);

        // Act
        await ChangePasswordAsync(email);

        // Assert: the security stamp check refuses both and signs them out, which deletes their rows
        (await AuthorizeAsync(rememberedDevice)).Location.ShouldContain("/Account/Login");
        (await AuthorizeAsync(shortSessionDevice)).Location.ShouldContain("/Account/Login");
        (await SessionsOfAsync(userId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Authorize_ShouldSendACookieWrittenBeforeTheSessionStoreToTheLoginForm()
    {
        // Arrange: the cookie as this server wrote it before ADR-0062, the whole principal inside and no
        // session key. Everything in it is still true, so its shape is the only thing wrong with it.
        (string email, _) = await RegisterUserAsync();
        string oldCookie = await WriteCookieWithoutASessionKeyAsync(email);

        // Act
        (HttpStatusCode authorizeStatus, string loginLocation) = await AuthorizeAsync(oldCookie);
        using HttpResponseMessage loginPage = await SendAsync(HttpMethod.Get, loginLocation, oldCookie);

        // Assert
        authorizeStatus.ShouldBe(HttpStatusCode.Redirect);
        loginLocation.ShouldContain("/Account/Login");
        loginPage.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await loginPage.Content.ReadAsStringAsync()).ShouldContain("Zaloguj się");
    }

    [Fact]
    public async Task SignIn_ShouldNotBringBackAStolenCopy_WhenTheBrowserStillHoldsTheOldCookie()
    {
        // Arrange: a copy taken before a password change, and the victim's browser still holds the old
        // cookie when they sign in again. The reset page links straight to the login form.
        (string email, _) = await RegisterUserAsync();
        string oldCookie = await SignInAsync(email, rememberMe: true);
        string stolenCopy = oldCookie;
        await ChangePasswordAsync(email);

        // Act
        string newCookie = await SignInAsync(email, rememberMe: true, browserCookie: oldCookie, password: NewPassword);

        // Assert
        (await AuthorizeAsync(newCookie)).Location.ShouldContain("code=");
        (HttpStatusCode copyStatus, string copyLocation) = await AuthorizeAsync(stolenCopy);
        copyStatus.ShouldBe(HttpStatusCode.Redirect);
        copyLocation.ShouldContain("/Account/Login");
    }

    [Fact]
    public async Task SignIn_ShouldStartASessionOfTheNewUser_WhenTheBrowserStillHoldsAnotherUsersCookie()
    {
        // Arrange: a shared computer, and the first user never signed out
        (string firstEmail, Guid firstUserId) = await RegisterUserAsync();
        (string secondEmail, Guid secondUserId) = await RegisterUserAsync();
        string firstUsersCookie = await SignInAsync(firstEmail, rememberMe: true);

        // Act
        string secondUsersCookie = await SignInAsync(secondEmail, rememberMe: true, browserCookie: firstUsersCookie);

        // Assert: a copy of the first user's cookie must not be signed in as the second user
        SignInSession secondUsersSession = (await SessionsOfAsync(secondUserId)).ShouldHaveSingleItem();
        SessionKeyOf(secondUsersCookie).ShouldBe(secondUsersSession.Id.ToString("N"));
        (await SessionsOfAsync(firstUserId)).ShouldBeEmpty();
        (await AuthorizeAsync(firstUsersCookie)).Location.ShouldContain("/Account/Login");
    }

    /// <summary>
    /// A browser can hold a cookie that names no live session. On the day of the deploy every remembered
    /// browser holds one from before the session store: the authorize step sends it to the login form,
    /// but the cookie stays and rides along with the login POST.
    /// </summary>
    [Theory]
    [InlineData(BrowserCookieKind.WrittenBeforeTheSessionStore)]
    [InlineData(BrowserCookieKind.Unreadable)]
    [InlineData(BrowserCookieKind.AlreadySignedOut)]
    public async Task SignIn_ShouldStartANewSession_WhenTheBrowserCookieNamesNoLiveSession(BrowserCookieKind kind)
    {
        // Arrange
        (string email, Guid userId) = await RegisterUserAsync();
        string browserCookie = await BrowserCookieAsync(kind, email);

        // Act
        string newCookie = await SignInAsync(email, rememberMe: true, browserCookie: browserCookie);

        // Assert
        (await AuthorizeAsync(newCookie)).Location.ShouldContain("code=");
        SignInSession session = (await SessionsOfAsync(userId)).ShouldHaveSingleItem();
        SessionKeyOf(newCookie).ShouldBe(session.Id.ToString("N"));
    }

    [Fact]
    public async Task SignIn_ShouldPutOnlyTheSessionKeyInTheCookie()
    {
        // Arrange
        (string email, Guid userId) = await RegisterUserAsync();

        // Act
        string authCookie = await SignInAsync(email, rememberMe: true);

        // Assert: no name, e-mail, user id or security stamp travels in the cookie any more. The claim type
        // is pinned too, because the login page reads the key under it to end the browser's old session.
        AuthenticationTicket? cookieTicket = AuthCookieOptions.TicketDataFormat.Unprotect(CookieValueOf(authCookie));
        SignInSession session = (await SessionsOfAsync(userId)).ShouldHaveSingleItem();
        cookieTicket.ShouldNotBeNull();
        Claim sessionKeyClaim = cookieTicket.Principal.Claims.ShouldHaveSingleItem();
        sessionKeyClaim.Type.ShouldBe(SignInSessionCookie.SessionKeyClaimType);
        sessionKeyClaim.Value.ShouldBe(session.Id.ToString("N"));
    }

    [Fact]
    public async Task SignIn_ShouldStoreTheTicketEncrypted()
    {
        // Arrange
        (string email, Guid userId) = await RegisterUserAsync();

        // Act
        await SignInAsync(email, rememberMe: true);

        // Assert
        SignInSession session = (await SessionsOfAsync(userId)).ShouldHaveSingleItem();
        string storedBytes = Encoding.UTF8.GetString(session.ProtectedTicket);
        storedBytes.ShouldNotContain(email, Case.Insensitive);
        storedBytes.ShouldNotContain(userId.ToString(), Case.Insensitive);
    }

    [Theory]
    [InlineData(true, 30 * 24 * 60)]
    [InlineData(false, 30)]
    public async Task SignIn_ShouldKeepTheSessionExactlyAsLongAsTheCookie(bool rememberMe, int lifetimeMinutes)
    {
        // Arrange
        (string email, Guid userId) = await RegisterUserAsync();
        DateTimeOffset signInStarted = DateTimeOffset.UtcNow;

        // Act
        await SignInAsync(email, rememberMe);

        // Assert
        DateTimeOffset signInEnded = DateTimeOffset.UtcNow;
        SignInSession session = (await SessionsOfAsync(userId)).ShouldHaveSingleItem();
        session.ExpiresAt.ShouldBeInRange(
            signInStarted.AddMinutes(lifetimeMinutes) - ExpiryTolerance,
            signInEnded.AddMinutes(lifetimeMinutes) + ExpiryTolerance);
    }

    [Fact]
    public async Task Authorize_ShouldSendTheCookieToTheLoginForm_WhenItsStoredSessionCannotBeDecrypted()
    {
        // Arrange: a row no key can open, for example one written under a keyring that is gone
        (string email, Guid userId) = await RegisterUserAsync();
        string authCookie = await SignInAsync(email, rememberMe: true);
        await OverwriteStoredTicketsAsync(userId, [1, 2, 3]);

        // Act
        (HttpStatusCode status, string location) = await AuthorizeAsync(authCookie);

        // Assert: nothing can ever read that row again, so it goes now and not at its expiry
        status.ShouldBe(HttpStatusCode.Redirect);
        location.ShouldContain("/Account/Login");
        (await SessionsOfAsync(userId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Authorize_ShouldDeleteTheStoredSession_WhenItsCookieComesBackExpired()
    {
        // Arrange: a short session, 30 minutes without "Zapamiętaj mnie"
        (string email, Guid userId) = await RegisterUserAsync();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using WebApplicationFactory<Program> host = CreateHostWithCookieClock(() => now);
        string authCookie = await SignInAsync(email, rememberMe: false, host: host);
        now = now.AddMinutes(31);

        // Act
        (HttpStatusCode status, string location) = await AuthorizeAsync(authCookie, host);

        // Assert
        status.ShouldBe(HttpStatusCode.Redirect);
        location.ShouldContain("/Account/Login");
        (await SessionsOfAsync(userId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Authorize_ShouldMoveTheStoredExpiry_WhenTheCookieSlides()
    {
        // Arrange: past half of its 30 minutes, the next request renews the session
        (string email, Guid userId) = await RegisterUserAsync();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using WebApplicationFactory<Program> host = CreateHostWithCookieClock(() => now);
        string authCookie = await SignInAsync(email, rememberMe: false, host: host);
        now = now.AddMinutes(16);

        // Act
        (HttpStatusCode status, string location) = await AuthorizeAsync(authCookie, host);

        // Assert: without the move, the daily prune would delete a session that is still in use
        status.ShouldBe(HttpStatusCode.Redirect);
        location.ShouldContain("code=");
        SignInSession session = (await SessionsOfAsync(userId)).ShouldHaveSingleItem();
        session.ExpiresAt.ShouldBe(now.AddMinutes(30), ExpiryTolerance);
    }

    [Fact]
    public async Task RenewAsync_ShouldMoveTheStoredExpiry()
    {
        // Arrange
        (_, Guid userId) = await RegisterUserAsync();
        DateTimeOffset issuedUtc = DateTimeOffset.UtcNow;
        string key = await TicketStore.StoreAsync(TicketFor(userId, issuedUtc, issuedUtc.AddMinutes(30)));
        DateTimeOffset renewedExpiry = issuedUtc.AddHours(2);

        // Act
        await TicketStore.RenewAsync(key, TicketFor(userId, issuedUtc.AddMinutes(20), renewedExpiry));

        // Assert
        SignInSession session = (await SessionsOfAsync(userId)).ShouldHaveSingleItem();
        session.ExpiresAt.ShouldBe(renewedExpiry, ExpiryTolerance);
        AuthenticationTicket? renewedTicket = await TicketStore.RetrieveAsync(key);
        renewedTicket.ShouldNotBeNull();
        renewedTicket.Properties.ExpiresUtc.ShouldNotBeNull().ShouldBe(renewedExpiry, ExpiryTolerance);
    }

    [Fact]
    public async Task RenewAsync_ShouldNotBringBackASessionThatWasSignedOut()
    {
        // Arrange: a request read the session, and a sign-out in another tab deleted it before that
        // request's answer renewed it
        (_, Guid userId) = await RegisterUserAsync();
        DateTimeOffset issuedUtc = DateTimeOffset.UtcNow;
        string key = await TicketStore.StoreAsync(TicketFor(userId, issuedUtc, issuedUtc.AddMinutes(30)));
        await TicketStore.RemoveAsync(key);

        // Act
        await TicketStore.RenewAsync(key, TicketFor(userId, issuedUtc, issuedUtc.AddHours(1)));

        // Assert
        (await TicketStore.RetrieveAsync(key)).ShouldBeNull();
        (await SessionsOfAsync(userId)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-session-key")]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    [InlineData("3f2504e04f8911d39a0c0305e82c3301")]
    public async Task RetrieveAsync_ShouldFindNoSession_ForAKeyTheStoreNeverIssued(string key)
    {
        // Arrange: a real session exists, so an empty table cannot be why nothing is found
        (_, Guid userId) = await RegisterUserAsync();
        DateTimeOffset issuedUtc = DateTimeOffset.UtcNow;
        await TicketStore.StoreAsync(TicketFor(userId, issuedUtc, issuedUtc.AddMinutes(30)));

        // Act
        AuthenticationTicket? ticket = await TicketStore.RetrieveAsync(key);

        // Assert
        ticket.ShouldBeNull();
    }

    private SignInSessionTicketStore TicketStore =>
        Factory.Services.GetRequiredService<SignInSessionTicketStore>();

    private CookieAuthenticationOptions AuthCookieOptions =>
        Factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);

    private async Task<(string Email, Guid UserId)> RegisterUserAsync()
    {
        (RegisterRequest registerRequest, IdentityId identityId) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, Password);

        return (registerRequest.Email, identityId.Value);
    }

    /// <summary>
    /// Signs in through the login form, like a browser on a device of its own, and returns the sign-in
    /// server's cookie as <c>name=value</c>. <paramref name="browserCookie"/> is a sign-in cookie the
    /// browser already holds.
    /// </summary>
    private async Task<string> SignInAsync(
        string email,
        bool rememberMe,
        string? browserCookie = null,
        string password = Password,
        WebApplicationFactory<Program>? host = null)
    {
        using HttpClient device = CreateDevice(host);

        using HttpResponseMessage loginPage = await device.GetAsync(new Uri("/Account/Login", UriKind.Relative));
        loginPage.StatusCode.ShouldBe(HttpStatusCode.OK);
        string html = await loginPage.Content.ReadAsStringAsync();

        Dictionary<string, string> form = new()
        {
            ["Email"] = email,
            ["Password"] = password,
            ["RememberMe"] = rememberMe ? "true" : "false",
            ["__RequestVerificationToken"] = AntiForgeryTokenRegex().Match(html).Groups[1].Value
        };

        using FormUrlEncodedContent content = new(form);
        using HttpRequestMessage loginRequest = new(HttpMethod.Post, "/Account/Login") { Content = content };
        foreach (string cookie in loginPage.Headers.GetValues("Set-Cookie"))
        {
            loginRequest.Headers.Add("Cookie", cookie.Split(';')[0]);
        }

        if (browserCookie is not null)
        {
            loginRequest.Headers.Add("Cookie", browserCookie);
        }

        using HttpResponseMessage loginResponse = await device.SendAsync(loginRequest);
        loginResponse.StatusCode.ShouldBe(HttpStatusCode.Redirect);

        return loginResponse.Headers.GetValues("Set-Cookie")
            .Select(cookie => cookie.Split(';')[0])
            .Single(cookie => cookie.StartsWith(AuthCookieOptions.Cookie.Name + "=", StringComparison.Ordinal));
    }

    /// <summary>
    /// Finishes the website's sign-in with the cookie and returns the ID token the website later sends
    /// as its sign-out hint.
    /// </summary>
    private async Task<string> SignInToTheWebsiteAsync(string authCookie)
    {
        (string codeVerifier, string codeChallenge) = GeneratePkce();
        using HttpResponseMessage authorizeResponse =
            await SendAsync(HttpMethod.Get, BuildAuthorizeUrl(codeChallenge), authCookie);
        authorizeResponse.StatusCode.ShouldBe(HttpStatusCode.Redirect);

        NameValueCollection callbackQuery = HttpUtility.ParseQueryString(authorizeResponse.Headers.Location!.Query);
        using FormUrlEncodedContent tokenRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = callbackQuery["code"]!,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = WebClientId,
            ["code_verifier"] = codeVerifier
        });

        using HttpResponseMessage tokenResponse =
            await ApiClient.Http.PostAsync(new Uri("connect/token", UriKind.Relative), tokenRequest);
        tokenResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        using JsonDocument tokens = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
        return tokens.RootElement.GetProperty("id_token").GetString()!;
    }

    private async Task<(HttpStatusCode Status, string Location)> AuthorizeAsync(
        string authCookie,
        WebApplicationFactory<Program>? host = null)
    {
        (_, string codeChallenge) = GeneratePkce();
        using HttpResponseMessage response =
            await SendAsync(HttpMethod.Get, BuildAuthorizeUrl(codeChallenge), authCookie, host);
        return (response.StatusCode, response.Headers.Location?.ToString() ?? string.Empty);
    }

    private async Task<HttpResponseMessage> SignOutAsync(string? idTokenHint, string authCookie)
    {
        string logoutUrl =
            $"connect/logout?post_logout_redirect_uri={Uri.EscapeDataString(AuthSystemApiFactory.TestFrontendAppRoot)}";
        if (idTokenHint is not null)
        {
            logoutUrl += $"&id_token_hint={Uri.EscapeDataString(idTokenHint)}";
        }

        return await SendAsync(HttpMethod.Get, logoutUrl, authCookie);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string url,
        string authCookie,
        WebApplicationFactory<Program>? host = null)
    {
        using HttpClient device = CreateDevice(host);
        using HttpRequestMessage request = new(method, new Uri(url, UriKind.RelativeOrAbsolute));
        request.Headers.Add("Cookie", authCookie);
        return await device.SendAsync(request);
    }

    private async Task ChangePasswordAsync(string email)
    {
        string accessToken = await GetAccessTokenAsync(email, Password);

        using HttpRequestMessage request = new(HttpMethod.Post, "auth/change-password");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(new ChangePasswordRequest(Password, NewPassword));

        using HttpResponseMessage response = await ApiClient.Http.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task<string> BrowserCookieAsync(BrowserCookieKind kind, string email)
    {
        switch (kind)
        {
            case BrowserCookieKind.WrittenBeforeTheSessionStore:
                return await WriteCookieWithoutASessionKeyAsync(email);
            case BrowserCookieKind.Unreadable:
                return $"{AuthCookieOptions.Cookie.Name}=CfDJ8NotAProtectedTicket";
            case BrowserCookieKind.AlreadySignedOut:
                string signedOutCookie = await SignInAsync(email, rememberMe: true);
                using (HttpResponseMessage signOutResponse = await SignOutAsync(idTokenHint: null, signedOutCookie))
                {
                    signOutResponse.StatusCode.ShouldBe(HttpStatusCode.Redirect);
                }

                return signedOutCookie;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    /// <summary>
    /// Writes the sign-in server's cookie the way it was written before it had a session store.
    /// </summary>
    private async Task<string> WriteCookieWithoutASessionKeyAsync(string email)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser user = (await userManager.FindByEmailAsync(email)).ShouldNotBeNull();

        ClaimsIdentity identity = new(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.UserName!),
                new Claim(ClaimTypes.Email, user.Email!),
                new Claim(
                    userManager.Options.ClaimsIdentity.SecurityStampClaimType,
                    await userManager.GetSecurityStampAsync(user))
            ],
            IdentityConstants.ApplicationScheme);

        DateTimeOffset issuedUtc = DateTimeOffset.UtcNow;
        AuthenticationTicket ticket = new(
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IsPersistent = true,
                IssuedUtc = issuedUtc,
                ExpiresUtc = issuedUtc.AddDays(30)
            },
            IdentityConstants.ApplicationScheme);

        return $"{AuthCookieOptions.Cookie.Name}={AuthCookieOptions.TicketDataFormat.Protect(ticket)}";
    }

    private async Task<List<SignInSession>> SessionsOfAsync(Guid userId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.SignInSessions
            .AsNoTracking()
            .Where(session => session.UserId == userId)
            .ToListAsync();
    }

    private async Task OverwriteStoredTicketsAsync(Guid userId, byte[] protectedTicket)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        int overwritten = await db.SignInSessions
            .Where(session => session.UserId == userId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(session => session.ProtectedTicket, protectedTicket));
        overwritten.ShouldBe(1);
    }

    private HttpClient CreateDevice(WebApplicationFactory<Program>? host = null) =>
        (host ?? Factory).CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });

    /// <summary>
    /// This host with the sign-in cookie on a clock the test moves. Each host protects cookies with its
    /// own keys, so a test signs in and authorizes through the same host.
    /// </summary>
    private WebApplicationFactory<Program> CreateHostWithCookieClock(Func<DateTimeOffset> utcNow)
    {
        TimeProvider clock = Substitute.For<TimeProvider>();
        clock.GetUtcNow().Returns(_ => utcNow());

        return Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                // A second relay on this database could take a row another test waits for.
                AuthSystemApiFactory.RemoveHostedService<OutboxRelay>(services);
                services.Configure<CookieAuthenticationOptions>(
                    IdentityConstants.ApplicationScheme,
                    options => options.TimeProvider = clock);
            }));
    }

    private static AuthenticationTicket TicketFor(Guid userId, DateTimeOffset issuedUtc, DateTimeOffset expiresUtc) =>
        new(
            new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
                IdentityConstants.ApplicationScheme)),
            new AuthenticationProperties { IssuedUtc = issuedUtc, ExpiresUtc = expiresUtc },
            IdentityConstants.ApplicationScheme);

    private string SessionKeyOf(string authCookie) =>
        AuthCookieOptions.TicketDataFormat.Unprotect(CookieValueOf(authCookie))
            .ShouldNotBeNull()
            .Principal.Claims.ShouldHaveSingleItem().Value;

    private static string CookieValueOf(string authCookie) =>
        authCookie[(authCookie.IndexOf('=', StringComparison.Ordinal) + 1)..];

    private static string BuildAuthorizeUrl(string codeChallenge) =>
        "connect/authorize?response_type=code" +
        $"&client_id={WebClientId}" +
        $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
        $"&scope={Uri.EscapeDataString("openid email profile roles api offline_access")}" +
        $"&code_challenge={codeChallenge}" +
        "&code_challenge_method=S256";

    private static (string CodeVerifier, string CodeChallenge) GeneratePkce()
    {
        string codeVerifier = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        string codeChallenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
        return (codeVerifier, codeChallenge);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [GeneratedRegex("""name="__RequestVerificationToken".*?value="([^"]+)""")]
    private static partial Regex AntiForgeryTokenRegex();

    public enum BrowserCookieKind
    {
        WrittenBeforeTheSessionStore,
        Unreadable,
        AlreadySignedOut
    }
}
