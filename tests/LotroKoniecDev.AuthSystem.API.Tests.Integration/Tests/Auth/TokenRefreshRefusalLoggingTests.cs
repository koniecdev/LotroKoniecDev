using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LotroKoniecDev.AuthSystem.API.BackgroundServices;
using LotroKoniecDev.AuthSystem.API.Features.Auth;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.Tests.Shared;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// #944: every refused refresh gets the same answer, so the client learns nothing about the account, and
/// only the sign-in server's own log names the case. <see cref="TokenEndpointTests"/> and
/// <see cref="SecurityStampTokenValidationTests"/> pin that answer. Nothing here revokes a token, so the
/// token row stays valid and the handler's own checks do the refusing. Tokens are sealed with each host's
/// own keys, so every test signs in on the host whose log it reads.
/// </summary>
public sealed class TokenRefreshRefusalLoggingTests : EndpointsTestBase
{
    private const string Password = "TestPass1!";
    private const string ClientId = "lotrokoniecdev-test";
    private const string OfflineScopes = "email profile roles api offline_access";

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
    [InlineData(AccountChange.SecurityStampChanged, EventIds.RefreshRefusedSecurityStampChanged, "the security stamp changed after sign-in, so a flow that ends every session has run")]
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
        string refreshToken = await SignInAsync(client, user.Email);

        await ApplyAsync(change, user.Email);

        // Act
        using HttpResponseMessage response = await RefreshAsync(client, refreshToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        CapturingLoggerFactory.LogEntry warning = TokenEndpointEntries(loggerFactory).ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.EventId.Id.ShouldBe(expectedEventId);
        warning.Message.ShouldBe($"Refresh refused for user {userId.Value}: {expectedCase}");
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
        string refreshToken = await SignInAsync(client, user.Email);

        // Act
        using HttpResponseMessage response = await RefreshAsync(client, refreshToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        TokenEndpointEntries(loggerFactory).ShouldBeEmpty();
    }

    private async Task ApplyAsync(AccountChange change, string email)
    {
        switch (change)
        {
            case AccountChange.Deleted:
                await ChangeUserAsync(email, static (userManager, user) => userManager.DeleteAsync(user));
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
                await ChangeUserAsync(email, static (userManager, user) => userManager.UpdateSecurityStampAsync(user));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(change), change, null);
        }
    }

    private async Task ChangeUserAsync(
        string email,
        Func<UserManager<ApplicationUser>, ApplicationUser, Task<IdentityResult>> change)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser? user = await userManager.FindByEmailAsync(email);
        user.ShouldNotBeNull();

        (await change(userManager, user)).Succeeded.ShouldBeTrue();
    }

    private static List<CapturingLoggerFactory.LogEntry> TokenEndpointEntries(CapturingLoggerFactory loggerFactory) =>
        loggerFactory.Entries
            .Where(entry => entry.Category == typeof(TokenEndpoint).FullName)
            .ToList();

    private static async Task<string> SignInAsync(HttpClient client, string email)
    {
        using FormUrlEncodedContent loginRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = email,
            ["password"] = Password,
            ["client_id"] = ClientId,
            ["scope"] = OfflineScopes
        });

        using HttpResponseMessage loginResponse = await client.PostAsync(
            new Uri("connect/token", UriKind.Relative), loginRequest);
        loginResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        using JsonDocument json = JsonDocument.Parse(await loginResponse.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("refresh_token").GetString()!;
    }

    private static async Task<HttpResponseMessage> RefreshAsync(HttpClient client, string refreshToken)
    {
        using FormUrlEncodedContent refreshRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = ClientId
        });

        return await client.PostAsync(new Uri("connect/token", UriKind.Relative), refreshRequest);
    }

    private WebApplicationFactory<Program> CreateHost(CapturingLoggerFactory loggerFactory) =>
        Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                // A second relay on this database could take a row another test waits for.
                AuthSystemApiFactory.RemoveHostedService<OutboxRelay>(services);

                services.AddSingleton<ILoggerFactory>(loggerFactory);
            }));
}
