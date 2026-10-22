using System.Collections.Immutable;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Settings;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.SharedKernel.Authorization;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// The seeder registers the website as a sign-in client only when that client is missing. So each test
/// deletes it first, and puts the host's own client back afterwards, because the other tests in the
/// collection sign in through it (#1220).
/// </summary>
public sealed class WebClientSeedingTests : EndpointsTestBase
{
    private const string LocalAppRoot = AuthSystemApiFactory.TestFrontendAppRoot;
    private const string DeployedAppRoot = "https://app.lotro.test";

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task SeedAuthDatabase_WebClientMissing_RegistersExactlyTheConfiguredAddresses(string environmentName)
    {
        // Arrange
        WebClientSettings webClient = new()
        {
            RedirectUris = [LocalAppRoot + "/callback", DeployedAppRoot + "/callback"],
            PostLogoutRedirectUris = [LocalAppRoot, DeployedAppRoot + "/"]
        };
        await DeleteWebClientAsync();

        // Act
        await ReseedAsync(new FakeWebHostEnvironment(environmentName), webClient);

        // Assert
        WebClientAddresses? addresses = await ReadWebClientAddressesAsync();
        addresses.ShouldNotBeNull();
        addresses.RedirectUris.ShouldBe(webClient.RedirectUris, ignoreOrder: true);
        addresses.PostLogoutRedirectUris.ShouldBe(webClient.PostLogoutRedirectUris, ignoreOrder: true);
    }

    /// <summary>
    /// The startup check skips Development and Testing (ADR-0008 §3, note 6), so an empty list can reach
    /// the seeder there. The seed must still finish: a start that fails on it explains nothing.
    /// </summary>
    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public async Task SeedAuthDatabase_NoAddressesConfigured_RegistersTheWebClientWithoutAddresses(
        string environmentName)
    {
        // Arrange
        WebClientSettings webClient = new() { RedirectUris = [], PostLogoutRedirectUris = [] };
        await DeleteWebClientAsync();

        // Act
        await ReseedAsync(new FakeWebHostEnvironment(environmentName), webClient);

        // Assert
        WebClientAddresses? addresses = await ReadWebClientAddressesAsync();
        addresses.ShouldNotBeNull();
        addresses.RedirectUris.ShouldBeEmpty();
        addresses.PostLogoutRedirectUris.ShouldBeEmpty();
    }

    public override async Task DisposeAsync()
    {
        try
        {
            await DeleteWebClientAsync();
            await DatabaseSeederExtensions.SeedAuthDatabaseAsync(
                Factory.Services,
                Factory.Services.GetRequiredService<IWebHostEnvironment>());
        }
        finally
        {
            await base.DisposeAsync();
        }
    }

    public WebClientSeedingTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    private async Task ReseedAsync(IWebHostEnvironment environment, WebClientSettings webClient)
    {
        OpenIddictSettings hostSettings = Factory.Services.GetRequiredService<IOptions<OpenIddictSettings>>().Value;
        OpenIddictSettings settings = new()
        {
            Issuer = hostSettings.Issuer,
            InternalIssuer = hostSettings.InternalIssuer,
            AccessTokenLifetimeMinutes = hostSettings.AccessTokenLifetimeMinutes,
            RefreshTokenLifetimeMinutes = hostSettings.RefreshTokenLifetimeMinutes,
            EncryptionKey = hostSettings.EncryptionKey,
            SigningKey = hostSettings.SigningKey,
            ApiClientSecret = hostSettings.ApiClientSecret,
            WebClient = webClient
        };

        await DatabaseSeederExtensions.SeedAuthDatabaseAsync(
            new ServicesWithOpenIddictSettings(Factory.Services, settings),
            environment);
    }

    private async Task DeleteWebClientAsync()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        IOpenIddictApplicationManager applicationManager =
            scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();

        object? webClient = await applicationManager.FindByClientIdAsync(AuthConstants.ClientIds.Web);
        if (webClient is not null)
        {
            await applicationManager.DeleteAsync(webClient);
        }
    }

    private async Task<WebClientAddresses?> ReadWebClientAddressesAsync()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        IOpenIddictApplicationManager applicationManager =
            scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();

        object? webClient = await applicationManager.FindByClientIdAsync(AuthConstants.ClientIds.Web);
        if (webClient is null)
        {
            return null;
        }

        return new WebClientAddresses(
            await applicationManager.GetRedirectUrisAsync(webClient),
            await applicationManager.GetPostLogoutRedirectUrisAsync(webClient));
    }

    private sealed record WebClientAddresses(
        ImmutableArray<string> RedirectUris,
        ImmutableArray<string> PostLogoutRedirectUris);

    /// <summary>
    /// The test host's services with one change: the seeder reads the given OpenIddict settings. The
    /// seeder reads them from the services it is handed, and the host's own settings cannot be changed.
    /// </summary>
    private sealed class ServicesWithOpenIddictSettings : IServiceProvider
    {
        private readonly IServiceProvider _services;
        private readonly IOptions<OpenIddictSettings> _settings;

        public object? GetService(Type serviceType) =>
            serviceType == typeof(IOptions<OpenIddictSettings>) ? _settings : _services.GetService(serviceType);

        public ServicesWithOpenIddictSettings(IServiceProvider services, OpenIddictSettings settings)
        {
            _services = services;
            _settings = Microsoft.Extensions.Options.Options.Create(settings);
        }
    }
}
