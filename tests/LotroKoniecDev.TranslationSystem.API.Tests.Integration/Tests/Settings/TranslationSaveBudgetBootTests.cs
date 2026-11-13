using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Settings;

/// <summary>
/// #687: a budget value that would switch the guard or its alert off stops the boot. The full matrix
/// lives in <c>TranslationSaveBudgetSettingsValidatorTests</c> in the unit suite.
/// </summary>
[Collection("TranslationApi")]
public sealed class TranslationSaveBudgetBootTests
{
    private readonly TranslationSystemApiFactory _factory;

    [Theory]
    [InlineData("TranslationSaveBudget:OverwriteAlertThreshold", "101", "TranslationSaveBudget:OverwriteAlertThreshold must be between 1 and TranslationSaveBudget:OverwriteLimit.")]
    [InlineData("TranslationSaveBudget:SaveLimit", "0", "TranslationSaveBudget:SaveLimit must be at least 1.")]
    public void Boot_WithABudgetValueThatSwitchesItOff_ShouldFailNamingTheKey(string key, string value, string expectedError)
    {
        // Arrange
        using WebApplicationFactory<Program> host = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { key, value }
                });
            });
        });

        // Act
        OptionsValidationException exception = Should.Throw<OptionsValidationException>(() => host.CreateClient());

        // Assert
        exception.ToString().ShouldContain(expectedError, Case.Sensitive);
    }

    public TranslationSaveBudgetBootTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }
}
