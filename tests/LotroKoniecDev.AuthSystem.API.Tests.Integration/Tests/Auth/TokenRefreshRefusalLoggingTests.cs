using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
/// <see cref="SecurityStampTokenValidationTests"/> pin that answer. Nothing here revokes a token, so the
/// token row stays valid and the handler's own checks do the refusing. Every test signs in on the host
/// whose log it reads.
/// </summary>
public sealed class TokenRefreshRefusalLoggingTests : EndpointsTestBase
{
    private const string Password = "TestPass1!";

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

    [Theory]
    [InlineData(AccountChange.Deleted, EventIds.RefreshRefusedUserGone, "the account no longer exists")]
    [InlineData(AccountChange.DeletionScheduled, EventIds.RefreshRefusedDeletionScheduled, "account deletion is scheduled")]
    [InlineData(AccountChange.LockedOut, EventIds.RefreshRefusedLockedOut, "the account is locked out")]
    [InlineData(AccountChange.DeletionScheduledAndLockedOut, EventIds.RefreshRefusedDeletionScheduled, "account deletion is scheduled")]
    [InlineData(AccountChange.SecurityStampChanged, EventIds.RefreshRefusedStaleSecurityStamp, "the security stamp in the token is not current")]
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
        warning.EventId.Id.ShouldBe(EventIds.RefreshRefusedStaleSecurityStamp);
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
        warning.EventId.Id.ShouldBe(EventIds.RefreshRefusedNoSubject);
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

    private WebApplicationFactory<Program> CreateHost(
        CapturingLoggerFactory loggerFactory,
        Action<OpenIddictServerBuilder>? configureServer = null) =>
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
            }));
}
