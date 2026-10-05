using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using OpenIddict.Server;
using LotroKoniecDev.AuthSystem.API.BackgroundServices;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// The website keeps the refresh token in its session cookie, and that cookie ends after 8 idle hours.
/// So the token lives 9 hours from its last use: the cookie's idle time plus a margin (#1014). OpenIddict
/// runs on a stopped clock here, so the dates in the token rows can be compared exactly.
/// </summary>
public sealed class RefreshTokenLifetimeTests : EndpointsTestBase
{
    private const string Password = "TestPass1!";
    private const string ClientId = "lotrokoniecdev-test";

    private static readonly TimeSpan ExpectedLifetime = TimeSpan.FromHours(9);

    public RefreshTokenLifetimeTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public async Task PasswordGrant_ShouldStoreARefreshTokenThatExpiresNineHoursAfterItWasIssued()
    {
        // Arrange
        (RegisterRequest user, _) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, Password);

        FakeTimeProvider clock = new(WholeSecondNow());
        await using WebApplicationFactory<Program> host = CreateHostOnClock(clock);
        using HttpClient client = host.CreateClient();
        DateTimeOffset issuedAt = clock.GetUtcNow();

        // Act
        using HttpResponseMessage response = await RequestPasswordGrantAsync(client, user.Email);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string refreshToken = await ReadRefreshTokenAsync(response);
        (DateTimeOffset? createdAt, DateTimeOffset? expiresAt) =
            await OpenIddictTokenState.DatesOfAsync(host.Services, refreshToken);
        createdAt.ShouldBe(issuedAt);
        expiresAt.ShouldBe(issuedAt + ExpectedLifetime);
    }

    [Fact]
    public async Task RefreshTokenGrant_ShouldStoreANewTokenThatExpiresNineHoursAfterTheRefresh()
    {
        // Arrange
        (RegisterRequest user, _) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, Password);

        FakeTimeProvider clock = new(WholeSecondNow());
        await using WebApplicationFactory<Program> host = CreateHostOnClock(clock);
        using HttpClient client = host.CreateClient();

        string firstRefreshToken = await SignInAsync(client, user.Email);
        clock.Advance(TimeSpan.FromHours(1));
        DateTimeOffset refreshedAt = clock.GetUtcNow();

        // Act
        using HttpResponseMessage response = await RequestRefreshGrantAsync(client, firstRefreshToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string secondRefreshToken = await ReadRefreshTokenAsync(response);

        // A fixed expiry would keep the first token's date, one hour earlier, and sign a user who keeps
        // working out in the middle of the day.
        (DateTimeOffset? createdAt, DateTimeOffset? expiresAt) =
            await OpenIddictTokenState.DatesOfAsync(host.Services, secondRefreshToken);
        createdAt.ShouldBe(refreshedAt);
        expiresAt.ShouldBe(refreshedAt + ExpectedLifetime);
    }

    /// <summary>
    /// Only OpenIddict runs on the given clock. Everything else in the host keeps the real one.
    /// </summary>
    private WebApplicationFactory<Program> CreateHostOnClock(TimeProvider clock) =>
        Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                // A second relay on this database could take a row another test waits for.
                AuthSystemApiFactory.RemoveHostedService<OutboxRelay>(services);

                services.Configure<OpenIddictServerOptions>(options => options.TimeProvider = clock);
            }));

    /// <summary>
    /// The stopped clock starts at the real time, so the first token is not dated in the future. OpenIddict
    /// stores its dates to the whole second, so a clock on a whole second keeps them exact.
    /// </summary>
    private static DateTimeOffset WholeSecondNow()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond));
    }

    /// <summary>
    /// Tokens are sealed with this host's own keys, so they come from the host that will check them.
    /// </summary>
    private static async Task<string> SignInAsync(HttpClient client, string email)
    {
        using HttpResponseMessage loginResponse = await RequestPasswordGrantAsync(client, email);
        loginResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await ReadRefreshTokenAsync(loginResponse);
    }

    private static async Task<HttpResponseMessage> RequestPasswordGrantAsync(HttpClient client, string email)
    {
        using FormUrlEncodedContent loginRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = email,
            ["password"] = Password,
            ["client_id"] = ClientId,
            ["scope"] = "email profile roles api offline_access"
        });

        return await client.PostAsync(new Uri("connect/token", UriKind.Relative), loginRequest);
    }

    private static async Task<HttpResponseMessage> RequestRefreshGrantAsync(HttpClient client, string refreshToken)
    {
        using FormUrlEncodedContent refreshRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = ClientId
        });

        return await client.PostAsync(new Uri("connect/token", UriKind.Relative), refreshRequest);
    }

    private static async Task<string> ReadRefreshTokenAsync(HttpResponseMessage response)
    {
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("refresh_token").GetString()!;
    }
}
