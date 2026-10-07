using System.Collections.Specialized;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using LotroKoniecDev.AuthSystem.API.BackgroundServices;
using LotroKoniecDev.AuthSystem.API.Features.Auth;
using LotroKoniecDev.AuthSystem.API.Services.Sessions;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.Tests.Shared;
using OpenIddict.Abstractions;
using OpenIddict.Server;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// #977: the code exchange runs the same account checks as the refresh, and each refusal writes the same
/// warning, naming the step as a code exchange. The client still gets one answer for every case, which
/// <see cref="AuthorizationCodeFlowTests"/> pins. OpenIddict refuses a used, expired or wrongly proven code
/// before the handler runs, and that refusal names the user too. Every test signs in and exchanges the
/// code on the host whose log it reads.
/// </summary>
public sealed partial class TokenCodeExchangeRefusalLoggingTests : EndpointsTestBase
{
    private const string Password = "TestPass1!";
    private const string WebClientId = "lotrokoniecdev-web";
    private const string RedirectUri = AuthSystemApiFactory.TestFrontendAppRoot + "/callback";

    public TokenCodeExchangeRefusalLoggingTests(AuthSystemApiFactory appFactory) : base(appFactory)
    {
    }

    public enum AccountChange
    {
        Deleted,
        DeletionScheduled,
        LockedOut,
        DeletionScheduledAndLockedOut,
        SecurityStampChanged
    }

    [Theory]
    [InlineData(AccountChange.Deleted, EventIds.TokenGrantRefusedUserGone, "the account no longer exists")]
    [InlineData(AccountChange.DeletionScheduled, EventIds.TokenGrantRefusedDeletionScheduled, "account deletion is scheduled")]
    [InlineData(AccountChange.LockedOut, EventIds.TokenGrantRefusedLockedOut, "the account is locked out")]
    [InlineData(AccountChange.DeletionScheduledAndLockedOut, EventIds.TokenGrantRefusedDeletionScheduled, "account deletion is scheduled")]
    [InlineData(AccountChange.SecurityStampChanged, EventIds.TokenGrantRefusedStaleSecurityStamp, "the security stamp in the token is not current")]
    public async Task AuthorizationCodeGrant_WhenTheAccountChangedAfterAuthorize_ShouldWarnWithTheCase(
        AccountChange change,
        int expectedEventId,
        string expectedCase)
    {
        // Arrange: the real deletion flow also locks the account, so with both set the scheduled deletion wins
        (RegisterRequest user, IdentityId userId) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, Password);
        using CapturingLoggerFactory loggerFactory = new();
        await using WebApplicationFactory<Program> host = CreateHost(loggerFactory);
        using HttpClient client = host.CreateClient();
        (string code, string codeVerifier) = await ObtainAuthorizationCodeAsync(host, user.Email);

        await ApplyAsync(change, user.Email);

        // Act
        using HttpResponseMessage response = await ExchangeAsync(client, code, codeVerifier);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        CapturingLoggerFactory.LogEntry warning = TokenEndpointEntries(loggerFactory).ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.EventId.Id.ShouldBe(expectedEventId);
        warning.Message.ShouldBe($"Code exchange refused for user {userId.Value}: {expectedCase}");
    }

    [Fact]
    public async Task AuthorizationCodeGrant_WhenTheCodeNamesNoUser_ShouldWarnWithoutAUserId()
    {
        // Arrange
        (RegisterRequest user, _) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, Password);
        using CapturingLoggerFactory loggerFactory = new();
        await using WebApplicationFactory<Program> host = CreateHost(loggerFactory, RemoveSubjectFromTheCode);
        using HttpClient client = host.CreateClient();
        (string code, string codeVerifier) = await ObtainAuthorizationCodeAsync(host, user.Email);

        // Act
        using HttpResponseMessage response = await ExchangeAsync(client, code, codeVerifier);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        CapturingLoggerFactory.LogEntry warning = TokenEndpointEntries(loggerFactory).ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.EventId.Id.ShouldBe(EventIds.TokenGrantRefusedNoSubject);
        warning.Message.ShouldBe("Code exchange refused: no user id could be read from the authorization code");
    }

    [Fact]
    public async Task AuthorizationCodeGrant_WhenTheCodeNamesNoUser_ShouldGiveTheSameAnswerAsEveryOtherAccountCase()
    {
        // Arrange
        (RegisterRequest user, _) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, Password);
        using CapturingLoggerFactory loggerFactory = new();
        await using WebApplicationFactory<Program> host = CreateHost(loggerFactory, RemoveSubjectFromTheCode);
        using HttpClient client = host.CreateClient();
        (string code, string codeVerifier) = await ObtainAuthorizationCodeAsync(host, user.Email);

        // Act
        using HttpResponseMessage response = await ExchangeAsync(client, code, codeVerifier);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("error").GetString().ShouldBe("invalid_grant");
        body.RootElement.GetProperty("error_description").GetString().ShouldBe("The authorization code is no longer valid.");
    }

    [Fact]
    public async Task AuthorizationCodeGrant_WhenAllSessionsWereRevokedAfterAuthorize_ShouldWarnWithTheUserAndOpenIddictsReason()
    {
        // Arrange: a password reset, signing out everywhere and a scheduled deletion all revoke through the
        // session revoker, and that revokes a code still waiting to be exchanged too
        (RegisterRequest user, IdentityId userId) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, Password);
        using CapturingLoggerFactory loggerFactory = new();
        await using WebApplicationFactory<Program> host = CreateHost(loggerFactory);
        using HttpClient client = host.CreateClient();
        (string code, string codeVerifier) = await ObtainAuthorizationCodeAsync(host, user.Email);

        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IUserSessionRevoker>().RevokeAllAsync(userId.Value.ToString());
        }

        // Act
        using HttpResponseMessage response = await ExchangeAsync(client, code, codeVerifier);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        CapturingLoggerFactory.LogEntry warning = TokenEndpointEntries(loggerFactory).ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.EventId.Id.ShouldBe(EventIds.TokenGrantRefusedByOpenIddict);
        warning.Message.ShouldBe(
            $"Code exchange refused for user {userId.Value} by OpenIddict: The specified authorization code is no longer valid.");
    }

    [Fact]
    public async Task AuthorizationCodeGrant_WhenTheAccountIsUnchanged_ShouldNotWarn()
    {
        // Arrange
        (RegisterRequest user, _) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, Password);
        using CapturingLoggerFactory loggerFactory = new();
        await using WebApplicationFactory<Program> host = CreateHost(loggerFactory);
        using HttpClient client = host.CreateClient();
        (string code, string codeVerifier) = await ObtainAuthorizationCodeAsync(host, user.Email);

        // Act
        using HttpResponseMessage response = await ExchangeAsync(client, code, codeVerifier);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        TokenEndpointEntries(loggerFactory).ShouldBeEmpty();
    }

    [Fact]
    public async Task AuthorizationCodeGrant_WhenTheCodeVerifierIsWrong_ShouldWarnWithTheUserAndOpenIddictsReason()
    {
        // Arrange
        (RegisterRequest user, IdentityId userId) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, Password);
        using CapturingLoggerFactory loggerFactory = new();
        await using WebApplicationFactory<Program> host = CreateHost(loggerFactory);
        using HttpClient client = host.CreateClient();
        (string code, _) = await ObtainAuthorizationCodeAsync(host, user.Email);
        (string otherCodeVerifier, _) = GeneratePkce();

        // Act
        using HttpResponseMessage response = await ExchangeAsync(client, code, otherCodeVerifier);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        CapturingLoggerFactory.LogEntry warning = TokenEndpointEntries(loggerFactory).ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.EventId.Id.ShouldBe(EventIds.TokenGrantRefusedByOpenIddict);
        warning.Message.ShouldBe(
            $"Code exchange refused for user {userId.Value} by OpenIddict: The specified 'code_verifier' is invalid.");
    }

    [Fact]
    public async Task AuthorizationCodeGrant_WhenTheCodeIsSentASecondTime_ShouldWarnWithTheUserAndOpenIddictsReason()
    {
        // Arrange
        (RegisterRequest user, IdentityId userId) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, Password);
        using CapturingLoggerFactory loggerFactory = new();
        await using WebApplicationFactory<Program> host = CreateHost(loggerFactory);
        using HttpClient client = host.CreateClient();
        (string code, string codeVerifier) = await ObtainAuthorizationCodeAsync(host, user.Email);

        using (HttpResponseMessage firstExchange = await ExchangeAsync(client, code, codeVerifier))
        {
            firstExchange.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Act
        using HttpResponseMessage response = await ExchangeAsync(client, code, codeVerifier);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        CapturingLoggerFactory.LogEntry warning = TokenEndpointEntries(loggerFactory).ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.EventId.Id.ShouldBe(EventIds.TokenGrantRefusedByOpenIddict);
        warning.Message.ShouldBe(
            $"Code exchange refused for user {userId.Value} by OpenIddict: The specified authorization code has already been redeemed.");
    }

    [Fact]
    public async Task AuthorizationCodeGrant_WhenTheCodeHasExpired_ShouldWarnThatItExpired()
    {
        // Arrange: OpenIddict says "no longer valid" for an expired code too, so the warning must not
        // read like a revoke
        (RegisterRequest user, IdentityId userId) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, Password);
        FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
        using CapturingLoggerFactory loggerFactory = new();
        await using WebApplicationFactory<Program> host = CreateHost(loggerFactory, openIddictClock: clock);
        using HttpClient client = host.CreateClient();
        (string code, string codeVerifier) = await ObtainAuthorizationCodeAsync(host, user.Email);

        clock.Advance(
            host.Services.GetRequiredService<IOptions<OpenIddictServerOptions>>().Value.AuthorizationCodeLifetime!.Value
            + TimeSpan.FromSeconds(1));

        // Act
        using HttpResponseMessage response = await ExchangeAsync(client, code, codeVerifier);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        CapturingLoggerFactory.LogEntry warning = TokenEndpointEntries(loggerFactory).ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.EventId.Id.ShouldBe(EventIds.TokenGrantRefusedTokenExpired);
        warning.Message.ShouldBe($"Code exchange refused for user {userId.Value}: the authorization code has expired");
    }

    private async Task ApplyAsync(AccountChange change, string email)
    {
        switch (change)
        {
            case AccountChange.Deleted:
                await AccountStateFactory.DeleteAsync(Factory.Services, email);
                break;
            case AccountChange.DeletionScheduled:
                await AccountStateFactory.ScheduleDeletionAsync(Factory.Services, email);
                break;
            case AccountChange.LockedOut:
                await AccountStateFactory.LockOutAsync(Factory.Services, email);
                break;
            case AccountChange.DeletionScheduledAndLockedOut:
                await AccountStateFactory.ScheduleDeletionAsync(Factory.Services, email);
                await AccountStateFactory.LockOutAsync(Factory.Services, email);
                break;
            case AccountChange.SecurityStampChanged:
                await AccountStateFactory.ChangeSecurityStampAsync(Factory.Services, email);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(change), change, null);
        }
    }

    /// <summary>
    /// Signs in through the login form like a browser that keeps no cookies, then authorizes with the
    /// sign-in cookie. The code is sealed with the host's own keys, so it comes from the host that will
    /// check it.
    /// </summary>
    private static async Task<(string Code, string CodeVerifier)> ObtainAuthorizationCodeAsync(
        WebApplicationFactory<Program> host,
        string email)
    {
        using HttpClient browser = host.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });

        using HttpResponseMessage loginPage = await browser.GetAsync(new Uri("/Account/Login", UriKind.Relative));
        loginPage.StatusCode.ShouldBe(HttpStatusCode.OK);
        string html = await loginPage.Content.ReadAsStringAsync();

        using FormUrlEncodedContent loginForm = new(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = Password,
            ["__RequestVerificationToken"] = AntiForgeryTokenRegex().Match(html).Groups[1].Value
        });
        using HttpRequestMessage loginRequest = new(HttpMethod.Post, "/Account/Login") { Content = loginForm };
        foreach (string cookie in loginPage.Headers.GetValues("Set-Cookie"))
        {
            loginRequest.Headers.Add("Cookie", cookie.Split(';')[0]);
        }

        using HttpResponseMessage loginResponse = await browser.SendAsync(loginRequest);
        loginResponse.StatusCode.ShouldBe(HttpStatusCode.Redirect);

        (string codeVerifier, string codeChallenge) = GeneratePkce();
        using HttpRequestMessage authorizeRequest = new(HttpMethod.Get, BuildAuthorizeUrl(codeChallenge));
        foreach (string cookie in loginResponse.Headers.GetValues("Set-Cookie"))
        {
            authorizeRequest.Headers.Add("Cookie", cookie.Split(';')[0]);
        }

        using HttpResponseMessage authorizeResponse = await browser.SendAsync(authorizeRequest);
        authorizeResponse.StatusCode.ShouldBe(HttpStatusCode.Redirect);

        NameValueCollection callbackQuery = HttpUtility.ParseQueryString(authorizeResponse.Headers.Location!.Query);
        return (callbackQuery["code"]!, codeVerifier);
    }

    private static async Task<HttpResponseMessage> ExchangeAsync(HttpClient client, string code, string codeVerifier)
    {
        using FormUrlEncodedContent tokenRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = WebClientId,
            ["code_verifier"] = codeVerifier
        });

        return await client.PostAsync(new Uri("connect/token", UriKind.Relative), tokenRequest);
    }

    /// <summary>
    /// OpenIddict never signs in a principal without a subject, so the subject is taken out of the code only,
    /// after OpenIddict has built that code's principal.
    /// </summary>
    private static void RemoveSubjectFromTheCode(OpenIddictServerBuilder server) =>
        server.AddEventHandler<OpenIddictServerEvents.ProcessSignInContext>(handler =>
            handler
                .UseInlineHandler(context =>
                {
                    context.AuthorizationCodePrincipal?.RemoveClaims(OpenIddictConstants.Claims.Subject);
                    return ValueTask.CompletedTask;
                })
                .SetOrder(OpenIddictServerHandlers.PrepareAuthorizationCodePrincipal.Descriptor.Order + 1));

    private static List<CapturingLoggerFactory.LogEntry> TokenEndpointEntries(CapturingLoggerFactory loggerFactory) =>
        loggerFactory.Entries
            .Where(entry => entry.Category == typeof(TokenEndpoint).FullName)
            .ToList();

    /// <summary>
    /// With <paramref name="openIddictClock"/>, only OpenIddict runs on that clock.
    /// </summary>
    private WebApplicationFactory<Program> CreateHost(
        CapturingLoggerFactory loggerFactory,
        Action<OpenIddictServerBuilder>? configureServer = null,
        TimeProvider? openIddictClock = null) =>
        Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                // A second relay on this database could take a row another test waits for.
                AuthSystemApiFactory.RemoveHostedService<OutboxRelay>(services);

                services.AddSingleton<ILoggerFactory>(loggerFactory);

                if (configureServer is not null)
                {
                    services.AddOpenIddict().AddServer(configureServer);
                }

                if (openIddictClock is not null)
                {
                    services.Configure<OpenIddictServerOptions>(options => options.TimeProvider = openIddictClock);
                }
            }));

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
}
