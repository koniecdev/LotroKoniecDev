using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace LotroKoniecDev.Frontend.Tests.Integration.Tests.Settings;

/// <summary>
/// ADR-0054 §6: outside Development and Testing the Frontend refuses to boot when the caller key for
/// either API is missing or bad. The unit tests on the two validators cover every rule; these tests
/// prove that both checks run when the app starts (#915).
/// </summary>
public sealed class CallerKeyStartupTests : IClassFixture<StagingFrontendFactory>
{
    private readonly StagingFrontendFactory _factory;

    public CallerKeyStartupTests(StagingFrontendFactory factory)
    {
        _factory = factory;
    }

    public static TheoryData<string, string> CallerKeysTheBootRefuses => new()
    {
        { "AuthSystem:CallerKey", string.Empty },
        { "AuthSystem:CallerKey", new string('k', 31) },
        { "TranslationSystem:CallerKey", string.Empty },
        { "TranslationSystem:CallerKey", new string('k', 31) }
    };

    [Theory]
    [MemberData(nameof(CallerKeysTheBootRefuses))]
    public void Boot_WithAMissingOrShortCallerKey_ShouldFailNamingTheKey(string setting, string callerKey)
    {
        // Arrange
        using WebApplicationFactory<Program> badKeyHost = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting(setting, callerKey));

        // Act
        Exception exception = Should.Throw<Exception>(() => badKeyHost.CreateClient());

        // Assert
        exception.ToString().ShouldContain(setting);
    }

    [Fact]
    public void Boot_WithBothCallerKeysAtTheMinimumLength_ShouldStart()
    {
        // Arrange: the host the tests above start, with no bad key, so a bad key is the one reason they fail
        string minimumLengthKey = new('k', 32);
        using WebApplicationFactory<Program> host = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("AuthSystem:CallerKey", minimumLengthKey);
            builder.UseSetting("TranslationSystem:CallerKey", minimumLengthKey);
        });

        // Act & Assert
        using HttpClient client = Should.NotThrow(() => host.CreateClient());
    }
}
