using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace LotroKoniecDev.Frontend.Tests.Integration.Tests.Settings;

/// <summary>
/// ADR-0074. The validator unit tests cover every rule; these prove the check runs at startup.
/// </summary>
public sealed class ClientSecretStartupTests : IClassFixture<ProductionFrontendFactory>
{
    private const string Setting = "AuthSystem:ClientSecret";

    private readonly ProductionFrontendFactory _factory;

    // null removes the secret, like a box whose .env never sets it; the empty string is a secret set to nothing.
    public static TheoryData<string?> ClientSecretsTheBootRefuses => new()
    {
        null,
        string.Empty,
        "   ",
        new string('s', 31)
    };

    [Theory]
    [MemberData(nameof(ClientSecretsTheBootRefuses))]
    public void Boot_WithAMissingOrShortClientSecret_ShouldFailNamingTheSettingButNotTheValue(string? clientSecret)
    {
        // Arrange
        using WebApplicationFactory<Program> badSecretHost = CreateHost(clientSecret);

        // Act
        OptionsValidationException exception = Should.Throw<OptionsValidationException>(() => badSecretHost.CreateClient());

        // Assert
        exception.Message.ShouldContain(Setting, Case.Sensitive);
        if (!string.IsNullOrWhiteSpace(clientSecret))
        {
            exception.Message.ShouldNotContain(clientSecret);
        }
    }

    [Fact]
    public void Boot_WithAClientSecretOfTheMinimumLength_ShouldStart()
    {
        // Arrange: the host the tests above start, with no bad secret, so a bad secret is the one reason they fail
        using WebApplicationFactory<Program> host = CreateHost(new string('s', 32));

        // Act & Assert
        using HttpClient client = Should.NotThrow(() => host.CreateClient());
    }

    public ClientSecretStartupTests(ProductionFrontendFactory factory)
    {
        _factory = factory;
    }

    // The in-memory source is the only way to remove a setting the factory sets.
    private WebApplicationFactory<Program> CreateHost(string? clientSecret)
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?> { { Setting, clientSecret } });
            });
        });
    }
}
