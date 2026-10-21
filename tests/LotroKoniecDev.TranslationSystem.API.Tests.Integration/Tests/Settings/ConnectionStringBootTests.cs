using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Settings;

/// <summary>
/// #1157: proves that the connection string check runs at startup and that the boot error carries no part
/// of the password. The full matrix of values lives in <see cref="ConnectionStringSettingsValidatorTests"/>.
/// </summary>
[Collection("TranslationApi")]
public sealed class ConnectionStringBootTests
{
    private readonly TranslationSystemApiFactory _factory;

    [Fact]
    public void Boot_WithAPasswordThatHasAnUnquotedSemicolon_ShouldFailNamingTheKeyWithoutThePassword()
    {
        // Arrange: Npgsql reads "Wx3" as the name of a setting and would print it in its own error.
        using WebApplicationFactory<Program> unreadableConnectionHost = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "ConnectionStrings:TranslationDatabase", "Host=db;Database=lotro_translation;Username=deploy;Password=Zq8;Wx3=Kp5" }
                });
            });
        });

        // Act
        OptionsValidationException exception =
            Should.Throw<OptionsValidationException>(() => unreadableConnectionHost.CreateClient());

        // Assert: the boot log writes the whole exception, inner errors included.
        string bootError = exception.ToString();
        bootError.ShouldContain("ConnectionStrings:TranslationDatabase", Case.Sensitive);
        bootError.ShouldNotContain("Zq8", Case.Insensitive);
        bootError.ShouldNotContain("Wx3", Case.Insensitive);
        bootError.ShouldNotContain("Kp5", Case.Insensitive);
    }

    public ConnectionStringBootTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }
}
