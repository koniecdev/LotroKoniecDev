using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Settings;

/// <summary>
/// #1025: proves that the access token lifetime check is registered and runs at startup. The full matrix
/// of values lives in <see cref="OpenIddictSettingsValidatorTests"/>.
/// </summary>
[Collection("AuthApi")]
public sealed class AccessTokenLifetimeBootTests
{
    private readonly AuthSystemApiFactory _factory;

    public AccessTokenLifetimeBootTests(AuthSystemApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void Boot_WithAOneMinuteAccessTokenLifetime_ShouldFailNamingTheKey()
    {
        // Arrange: the lifetime is checked in every environment, so a Testing host with this one value
        // changed proves the wiring.
        using WebApplicationFactory<Program> shortLifetimeHost = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "OpenIddict:AccessTokenLifetimeMinutes", "1" }
                });
            });
        });

        // Act
        OptionsValidationException exception = Should.Throw<OptionsValidationException>(() => shortLifetimeHost.CreateClient());

        // Assert
        exception.Message.ShouldContain("OpenIddict:AccessTokenLifetimeMinutes", Case.Sensitive);
    }
}
