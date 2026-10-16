using System.Globalization;
using FluentValidation;
using FluentValidation.Results;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Settings;

/// <summary>
/// #1092: proves that the host turns off FluentValidation's own translations when it starts, so the
/// API answers in English on a server whose OS language is not English (ADR-0044 §1).
/// Every rule in this API has its own English message today, so no endpoint can show the default
/// messages. A rule without a message of its own can.
/// </summary>
[Collection("AuthApi")]
public sealed class ValidationMessageLanguageBootTests
{
    private readonly AuthSystemApiFactory _factory;

    [Theory]
    [InlineData("pl-PL")]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    public void Boot_OnAServerInAnotherLanguage_ShouldKeepDefaultValidationMessagesInEnglish(string serverCulture)
    {
        // Arrange: reading Services makes sure the host has started, so Program has run.
        _ = _factory.Services;
        InlineValidator<Probe> validator = new() { rules => rules.RuleFor(probe => probe.Text).NotEmpty() };
        CultureInfo originalCulture = CultureInfo.CurrentUICulture;

        // Act
        ValidationResult result;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(serverCulture);
            result = validator.Validate(new Probe(string.Empty));
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }

        // Assert
        result.Errors.ShouldHaveSingleItem().ErrorMessage.ShouldBe("'Text' must not be empty.");
    }

    public ValidationMessageLanguageBootTests(AuthSystemApiFactory factory)
    {
        _factory = factory;
    }

    private sealed record Probe(string Text);
}
