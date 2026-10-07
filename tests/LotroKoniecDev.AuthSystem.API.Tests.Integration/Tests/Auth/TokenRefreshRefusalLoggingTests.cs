using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
/// #944: every refused refresh gets the same answer, so the client learns nothing about the account, and
/// only the sign-in server's own log names the case. <see cref="TokenEndpointTests"/> and
/// <see cref="SecurityStampTokenValidationTests"/> pin that answer. The account-change tests revoke
/// nothing, so the token row stays valid and the handler's own checks do the refusing. The revoke, expiry
/// and replay tests reach OpenIddict's own refusal, which runs before the handler (#977). Every test signs
/// in on the host whose log it reads.
/// </summary>
public sealed class TokenRefreshRefusalLoggingTests : EndpointsTestBase
{
    private const string Password = "TestPass1!";

    private static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromHours(9);

    public TokenRefreshRefusalLoggingTests(AuthSystemApiFactory appFactory) : base(appFactory)
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

    public enum SessionEnd
    {
        AllSessionsRevoked,
        OnlyAuthorizationsRevoked
    }

    [Theory]
    [InlineData(AccountChange.Deleted, EventIds.TokenGrantRefusedUserGone, "the account no longer exists")]
    [InlineData(AccountChange.DeletionScheduled, EventIds.TokenGrantRefusedDeletionScheduled, "account deletion is scheduled")]
    [InlineData(AccountChange.LockedOut, EventIds.TokenGrantRefusedLockedOut, "the account is locked out")]
    [InlineData(AccountChange.DeletionScheduledAndLockedOut, EventIds.TokenGrantRefusedDeletionScheduled, "account deletion is scheduled")]
    [InlineData(AccountChange.SecurityStampChanged, EventIds.TokenGrantRefusedStaleSecurityStamp, "the security stamp in the token is not current")]
    public async Task RefreshTokenGrant_WhenTheAccountChangedAfterSignIn_ShouldWarnWithTheCase(
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
        string refreshToken = await GetRefreshTokenAsync(client, user.Email, Password);

        await ApplyAsync(change, user.Email);

        // Act
        using HttpResponseMessage response = await RequestRefreshGrantAsync(client, refreshToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        CapturingLoggerFactory.LogEntry warning = TokenEndpointEntries(loggerFactory).ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.EventId.Id.ShouldBe(expectedEventId);
        warning.Message.ShouldBe($"Refresh refused for user {userId.Value}: {expectedCase}");
    }

    [Fact]
    public async Task RefreshTokenGrant_WhenTheTokenCarriesNoSecurityStamp_ShouldWarnThatTheStampIsNotCurrent()
    {
        // Arrange: a token from before #848 carries no stamp, and the stamp check refuses it as well
        (RegisterRequest user, IdentityId userId) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, Password);
        using CapturingLoggerFactory loggerFactory = new();
        await using WebApplicationFactory<Program> host = CreateHost(
            loggerFactory,
            server => server.AddEventHandler<OpenIddictServerEvents.ProcessSignInContext>(handler =>
                handler
                    .UseInlineHandler(context =>
                    {
                        context.Principal?.RemoveClaims(SessionSecurityStamp.ClaimType);
                        return ValueTask.CompletedTask;
                    })
                    .SetOrder(int.MinValue)));
        using HttpClient client = host.CreateClient();
        string refreshToken = await GetRefreshTokenAsync(client, user.Email, Password);

        // Act
        using HttpResponseMessage response = await RequestRefreshGrantAsync(client, refreshToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        CapturingLoggerFactory.LogEntry warning = TokenEndpointEntries(loggerFactory).ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.EventId.Id.ShouldBe(EventIds.TokenGrantRefusedStaleSecurityStamp);
        warning.Message.ShouldBe($"Refresh refused for user {userId.Value}: the security stamp in the token is not current");
    }

    [Fact]
    public async Task RefreshTokenGrant_WhenTheTokenNamesNoUser_ShouldWarnWithoutAUserId()
    {
        // Arrange: OpenIddict never signs in a principal without a subject, so the subject is taken out of
        // the refresh token only, after OpenIddict has built that token's principal
        (RegisterRequest user, _) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, Password);
        using CapturingLoggerFactory loggerFactory = new();
        await using WebApplicationFactory<Program> host = CreateHost(
            loggerFactory,
            server => server.AddEventHandler<OpenIddictServerEvents.ProcessSignInContext>(handler =>
                handler
                    .UseInlineHandler(context =>
                    {
                        context.RefreshTokenPrincipal?.RemoveClaims(OpenIddictConstants.Claims.Subject);
                        return ValueTask.CompletedTask;
                    })
                    .SetOrder(OpenIddictServerHandlers.PrepareRefreshTokenPrincipal.Descriptor.Order + 1)));
        using HttpClient client = host.CreateClient();
        string refreshToken = await GetRefreshTokenAsync(client, user.Email, Password);

        // Act
        using HttpResponseMessage response = await RequestRefreshGrantAsync(client, refreshToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        CapturingLoggerFactory.LogEntry warning = TokenEndpointEntries(loggerFactory).ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.EventId.Id.ShouldBe(EventIds.TokenGrantRefusedNoSubject);
        warning.Message.ShouldBe("Refresh refused: no user id could be read from the refresh token");
    }

    [Fact]
    public async Task RefreshTokenGrant_WhenTheAccountIsUnchanged_ShouldNotWarn()
    {
        // Arrange
        (RegisterRequest user, _) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, Password);
        using CapturingLoggerFactory loggerFactory = new();
        await using WebApplicationFactory<Program> host = CreateHost(loggerFactory);
        using HttpClient client = host.CreateClient();
        string refreshToken = await GetRefreshTokenAsync(client, user.Email, Password);

        // Act
        using HttpResponseMessage response = await RequestRefreshGrantAsync(client, refreshToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        TokenEndpointEntries(loggerFactory).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(SessionEnd.AllSessionsRevoked, "The specified refresh token is no longer valid.")]
    [InlineData(SessionEnd.OnlyAuthorizationsRevoked, "The authorization associated with the refresh token is no longer valid.")]
    public async Task RefreshTokenGrant_WhenTheSessionWasRevoked_ShouldWarnWithTheUserAndOpenIddictsReason(
        SessionEnd end,
        string expectedReason)
    {
        // Arrange: signing out everywhere, a password change and a scheduled deletion all revoke through
        // the session revoker; a revoke whose token step failed leaves only the authorizations revoked
        (RegisterRequest user, IdentityId userId) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, Password);
        using CapturingLoggerFactory loggerFactory = new();
        await using WebApplicationFactory<Program> host = CreateHost(loggerFactory);
        using HttpClient client = host.CreateClient();
        string refreshToken = await GetRefreshTokenAsync(client, user.Email, Password);

        await EndAsync(end, host.Services, userId.Value.ToString());

        // Act
        using HttpResponseMessage response = await RequestRefreshGrantAsync(client, refreshToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        CapturingLoggerFactory.LogEntry warning = TokenEndpointEntries(loggerFactory).ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.EventId.Id.ShouldBe(EventIds.TokenGrantRefusedByOpenIddict);
        warning.Message.ShouldBe($"Refresh refused for user {userId.Value} by OpenIddict: {expectedReason}");
    }

    [Theory]
    [InlineData(SessionEnd.AllSessionsRevoked, "The specified refresh token is no longer valid.")]
    [InlineData(SessionEnd.OnlyAuthorizationsRevoked, "The authorization associated with the refresh token is no longer valid.")]
    public async Task RefreshTokenGrant_WhenTheSessionWasRevoked_ShouldKeepOpenIddictsAnswer(
        SessionEnd end,
        string expectedDescription)
    {
        // Arrange: the warning is for the server's log only, so the client still gets OpenIddict's answer
        (RegisterRequest user, IdentityId userId) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, Password);
        using CapturingLoggerFactory loggerFactory = new();
        await using WebApplicationFactory<Program> host = CreateHost(loggerFactory);
        using HttpClient client = host.CreateClient();
        string refreshToken = await GetRefreshTokenAsync(client, user.Email, Password);

        await EndAsync(end, host.Services, userId.Value.ToString());

        // Act
        using HttpResponseMessage response = await RequestRefreshGrantAsync(client, refreshToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("error").GetString().ShouldBe("invalid_grant");
        body.RootElement.GetProperty("error_description").GetString().ShouldBe(expectedDescription);
    }

    [Fact]
    public async Task RefreshTokenGrant_WhenTheTokenHasExpired_ShouldWarnThatItExpired()
    {
        // Arrange: OpenIddict says "no longer valid" for an expired token too, so the warning must not
        // read like a revoke
        (RegisterRequest user, IdentityId userId) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, Password);
        FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
        using CapturingLoggerFactory loggerFactory = new();
        await using WebApplicationFactory<Program> host = CreateHost(loggerFactory, openIddictClock: clock);
        using HttpClient client = host.CreateClient();
        string refreshToken = await GetRefreshTokenAsync(client, user.Email, Password);

        clock.Advance(RefreshTokenLifetime + TimeSpan.FromSeconds(1));

        // Act
        using HttpResponseMessage response = await RequestRefreshGrantAsync(client, refreshToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        CapturingLoggerFactory.LogEntry warning = TokenEndpointEntries(loggerFactory).ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.EventId.Id.ShouldBe(EventIds.TokenGrantRefusedTokenExpired);
        warning.Message.ShouldBe($"Refresh refused for user {userId.Value}: the refresh token has expired");
    }

    [Fact]
    public async Task RefreshTokenGrant_WhenAUsedTokenIsSentAgainAfterTheReuseWindow_ShouldWarnWithTheUserAndOpenIddictsReason()
    {
        // Arrange: OpenIddict accepts a used refresh token again for 30 seconds, then refuses it
        (RegisterRequest user, IdentityId userId) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, Password);
        FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
        using CapturingLoggerFactory loggerFactory = new();
        await using WebApplicationFactory<Program> host = CreateHost(loggerFactory, openIddictClock: clock);
        using HttpClient client = host.CreateClient();
        string refreshToken = await GetRefreshTokenAsync(client, user.Email, Password);

        using (HttpResponseMessage firstRefresh = await RequestRefreshGrantAsync(client, refreshToken))
        {
            firstRefresh.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        clock.Advance(TimeSpan.FromMinutes(1));

        // Act
        using HttpResponseMessage response = await RequestRefreshGrantAsync(client, refreshToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        CapturingLoggerFactory.LogEntry warning = TokenEndpointEntries(loggerFactory).ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.EventId.Id.ShouldBe(EventIds.TokenGrantRefusedByOpenIddict);
        warning.Message.ShouldBe(
            $"Refresh refused for user {userId.Value} by OpenIddict: The specified refresh token has already been redeemed.");
    }

    [Theory]
    [InlineData("not-a-refresh-token")]
    [InlineData("x5ccrEJ8dfJXiYSyBg2rdMHGfHHGJAiMR4ixtWsVQ1Y")]
    public async Task RefreshTokenGrant_WhenTheTokenIsUnknown_ShouldNotWarn(string refreshToken)
    {
        // Arrange: no token row and no readable token, so there is no user to name and OpenIddict's own
        // line is the whole story
        using CapturingLoggerFactory loggerFactory = new();
        await using WebApplicationFactory<Program> host = CreateHost(loggerFactory);
        using HttpClient client = host.CreateClient();

        // Act
        using HttpResponseMessage response = await RequestRefreshGrantAsync(client, refreshToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        TokenEndpointEntries(loggerFactory).ShouldBeEmpty();
    }

    private static async Task EndAsync(SessionEnd end, IServiceProvider services, string userId)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        switch (end)
        {
            case SessionEnd.AllSessionsRevoked:
                await scope.ServiceProvider.GetRequiredService<IUserSessionRevoker>().RevokeAllAsync(userId);
                break;
            case SessionEnd.OnlyAuthorizationsRevoked:
                await scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>().RevokeBySubjectAsync(userId);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(end), end, null);
        }
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
}
