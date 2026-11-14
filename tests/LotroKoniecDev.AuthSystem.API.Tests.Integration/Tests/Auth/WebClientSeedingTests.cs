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
/// The seeder registers each website client only when it is missing, so each test deletes both first and
/// restores the host's clients afterwards: the other tests sign in through them (#1220). The old public
/// client and the confidential one share their addresses and grants (ADR-0074).
/// </summary>
public sealed class WebClientSeedingTests : EndpointsTestBase
{
    private const string LocalAppRoot = AuthSystemApiFactory.TestFrontendAppRoot;
    private const string DeployedAppRoot = "https://app.lotro.test";

    [Theory]
    [InlineData("Development", AuthConstants.ClientIds.Web)]
    [InlineData("Testing", AuthConstants.ClientIds.Web)]
    [InlineData("Staging", AuthConstants.ClientIds.Web)]
    [InlineData("Production", AuthConstants.ClientIds.Web)]
    [InlineData("Development", AuthConstants.ClientIds.Frontend)]
    [InlineData("Testing", AuthConstants.ClientIds.Frontend)]
    [InlineData("Staging", AuthConstants.ClientIds.Frontend)]
    [InlineData("Production", AuthConstants.ClientIds.Frontend)]
    public async Task SeedAuthDatabase_WebClientMissing_RegistersExactlyTheConfiguredAddresses(
        string environmentName,
        string clientId)
    {
        // Arrange
        WebClientSettings webClient = new()
        {
            RedirectUris = [LocalAppRoot + "/callback", DeployedAppRoot + "/callback"],
            PostLogoutRedirectUris = [LocalAppRoot, DeployedAppRoot + "/"]
        };
        await DeleteWebsiteClientsAsync();

        // Act
        await ReseedAsync(new FakeWebHostEnvironment(environmentName), webClient);

        // Assert
        WebClientAddresses? addresses = await ReadWebClientAddressesAsync(clientId);
        addresses.ShouldNotBeNull();
        addresses.RedirectUris.ShouldBe(webClient.RedirectUris, ignoreOrder: true);
        addresses.PostLogoutRedirectUris.ShouldBe(webClient.PostLogoutRedirectUris, ignoreOrder: true);
    }

    /// <summary>
    /// The startup check skips Development and Testing (ADR-0008 §3, note 6), so an empty list can reach
    /// the seeder there. The seed must still finish: a start that fails on it explains nothing.
    /// </summary>
    [Theory]
    [InlineData("Development", AuthConstants.ClientIds.Web)]
    [InlineData("Testing", AuthConstants.ClientIds.Web)]
    [InlineData("Development", AuthConstants.ClientIds.Frontend)]
    [InlineData("Testing", AuthConstants.ClientIds.Frontend)]
    public async Task SeedAuthDatabase_NoAddressesConfigured_RegistersTheWebClientWithoutAddresses(
        string environmentName,
        string clientId)
    {
        // Arrange
        WebClientSettings webClient = new() { RedirectUris = [], PostLogoutRedirectUris = [] };
        await DeleteWebsiteClientsAsync();

        // Act
        await ReseedAsync(new FakeWebHostEnvironment(environmentName), webClient);

        // Assert
        WebClientAddresses? addresses = await ReadWebClientAddressesAsync(clientId);
        addresses.ShouldNotBeNull();
        addresses.RedirectUris.ShouldBeEmpty();
        addresses.PostLogoutRedirectUris.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(AuthConstants.ClientIds.Web, OpenIddictConstants.ClientTypes.Public)]
    [InlineData(AuthConstants.ClientIds.Frontend, OpenIddictConstants.ClientTypes.Confidential)]
    public async Task SeedAuthDatabase_WebsiteClientsMissing_RegistersEachWithItsClientType(
        string clientId,
        string expectedClientType)
    {
        // Arrange
        await DeleteWebsiteClientsAsync();

        // Act
        await ReseedAsync(new FakeWebHostEnvironment("Production"), HostWebClientSettings());

        // Assert
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        IOpenIddictApplicationManager applicationManager =
            scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        object client = (await applicationManager.FindByClientIdAsync(clientId)).ShouldNotBeNull();
        (await applicationManager.GetClientTypeAsync(client)).ShouldBe(expectedClientType);
    }

    /// <summary>
    /// The token endpoint gives a service token to any client with the client credentials grant, so neither
    /// website client may hold it.
    /// </summary>
    [Fact]
    public async Task SeedAuthDatabase_WebsiteClientsMissing_GivesBothTheSamePermissionsWithoutClientCredentials()
    {
        // Arrange
        await DeleteWebsiteClientsAsync();

        // Act
        await ReseedAsync(new FakeWebHostEnvironment("Production"), HostWebClientSettings());

        // Assert
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        IOpenIddictApplicationManager applicationManager =
            scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        object oldClient = (await applicationManager.FindByClientIdAsync(AuthConstants.ClientIds.Web)).ShouldNotBeNull();
        object newClient = (await applicationManager.FindByClientIdAsync(AuthConstants.ClientIds.Frontend)).ShouldNotBeNull();
        ImmutableArray<string> newPermissions = await applicationManager.GetPermissionsAsync(newClient);
        newPermissions.ShouldBe(await applicationManager.GetPermissionsAsync(oldClient), ignoreOrder: true);
        newPermissions.ShouldNotContain(OpenIddictConstants.Permissions.GrantTypes.ClientCredentials);
    }

    /// <summary>
    /// The startup check skips Development and Testing, so a blank secret can reach the seeder there.
    /// OpenIddict refuses a confidential client with no secret, so the start fails loudly instead of
    /// running with a website that cannot sign in.
    /// </summary>
    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public async Task SeedAuthDatabase_FrontendClientMissingAndNoSecret_StopsTheSeed(string environmentName)
    {
        // Arrange
        await DeleteWebsiteClientsAsync();

        // Act & Assert
        await Should.ThrowAsync<OpenIddictExceptions.ValidationException>(() =>
            ReseedAsync(new FakeWebHostEnvironment(environmentName), HostWebClientSettings(), frontendClientSecret: string.Empty));
    }

    public override async Task DisposeAsync()
    {
        try
        {
            await DeleteWebsiteClientsAsync();
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

    private async Task ReseedAsync(
        IWebHostEnvironment environment,
        WebClientSettings webClient,
        string? frontendClientSecret = null)
    {
        OpenIddictSettings hostSettings = Factory.Services.GetRequiredService<IOptions<OpenIddictSettings>>().Value;
        OpenIddictSettings settings = new()
        {
            Issuer = hostSettings.Issuer,
            AccessTokenLifetimeMinutes = hostSettings.AccessTokenLifetimeMinutes,
            RefreshTokenLifetimeMinutes = hostSettings.RefreshTokenLifetimeMinutes,
            EncryptionKey = hostSettings.EncryptionKey,
            SigningKey = hostSettings.SigningKey,
            ApiClientSecret = hostSettings.ApiClientSecret,
            FrontendClientSecret = frontendClientSecret ?? hostSettings.FrontendClientSecret,
            WebClient = webClient
        };

        await DatabaseSeederExtensions.SeedAuthDatabaseAsync(
            new ServicesWithOpenIddictSettings(Factory.Services, settings),
            environment);
    }

    private WebClientSettings HostWebClientSettings() =>
        Factory.Services.GetRequiredService<IOptions<OpenIddictSettings>>().Value.WebClient;

    private async Task DeleteWebsiteClientsAsync()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        IOpenIddictApplicationManager applicationManager =
            scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();

        foreach (string clientId in new[] { AuthConstants.ClientIds.Web, AuthConstants.ClientIds.Frontend })
        {
            object? client = await applicationManager.FindByClientIdAsync(clientId);
            if (client is not null)
            {
                await applicationManager.DeleteAsync(client);
            }
        }
    }

    private async Task<WebClientAddresses?> ReadWebClientAddressesAsync(string clientId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        IOpenIddictApplicationManager applicationManager =
            scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();

        object? webClient = await applicationManager.FindByClientIdAsync(clientId);
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
    /// The seeder reads the OpenIddict settings from the services it is handed, and the host's own settings
    /// cannot be changed.
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
