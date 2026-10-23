using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace LotroKoniecDev.Frontend.Tests.Integration.Tests.Settings;

/// <summary>
/// ADR-0054 §6. The validator unit tests cover every rule; these prove both checks run at startup (#915).
/// </summary>
public sealed class CallerKeyStartupTests : IClassFixture<ProductionFrontendFactory>
{
    private readonly ProductionFrontendFactory _factory;

    public CallerKeyStartupTests(ProductionFrontendFactory factory)
    {
        _factory = factory;
    }

    // null removes the key, like a box whose .env never sets it; the empty string is a key set to nothing.
    public static TheoryData<string, string?> CallerKeysTheBootRefuses => new()
    {
        { "AuthSystem:CallerKey", null },
        { "AuthSystem:CallerKey", string.Empty },
        { "AuthSystem:CallerKey", new string('k', 31) },
        { "TranslationSystem:CallerKey", null },
        { "TranslationSystem:CallerKey", string.Empty },
        { "TranslationSystem:CallerKey", new string('k', 31) }
    };

    [Theory]
    [MemberData(nameof(CallerKeysTheBootRefuses))]
    public void Boot_WithAMissingOrShortCallerKey_ShouldFailNamingTheKey(string setting, string? callerKey)
    {
        // Arrange
        using WebApplicationFactory<Program> badKeyHost = CreateHost(new Dictionary<string, string?>
        {
            { setting, callerKey }
        });

        // Act
        OptionsValidationException exception = Should.Throw<OptionsValidationException>(() => badKeyHost.CreateClient());

        // Assert
        exception.Message.ShouldContain(setting, Case.Sensitive);
    }

    [Fact]
    public void Boot_WithBothCallerKeysAtTheMinimumLength_ShouldStart()
    {
        // Arrange: the host the tests above start, with no bad key, so a bad key is the one reason they fail
        string minimumLengthKey = new('k', 32);
        using WebApplicationFactory<Program> host = CreateHost(new Dictionary<string, string?>
        {
            { "AuthSystem:CallerKey", minimumLengthKey },
            { "TranslationSystem:CallerKey", minimumLengthKey }
        });

        // Act & Assert
        using HttpClient client = Should.NotThrow(() => host.CreateClient());
    }

    // The in-memory source is the only way to remove a key the factory sets.
    private WebApplicationFactory<Program> CreateHost(Dictionary<string, string?> settings)
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(settings);
            });
        });
    }
}
