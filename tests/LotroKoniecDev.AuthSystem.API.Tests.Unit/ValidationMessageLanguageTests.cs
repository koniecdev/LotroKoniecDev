using System.Globalization;
using System.Runtime.CompilerServices;
using FluentValidation;
using FluentValidation.Results;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit;

/// <summary>
/// #1092: once any code of this API's assembly has run, the default messages stay English on a
/// machine whose OS has another language. No rule of this API uses a default message today, so a rule
/// written here stands in for one.
/// </summary>
public sealed class ValidationMessageLanguageTests
{
    [Theory]
    [InlineData("pl-PL")]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    public void KeepEnglish_OnAMachineInAnotherLanguage_ShouldLeaveDefaultMessagesInEnglish(string machineCulture)
    {
        // Arrange: an InlineValidator runs no code of this assembly, so the runtime may not have run
        // its initializer yet. Asking twice does nothing.
        RuntimeHelpers.RunModuleConstructor(typeof(ValidationMessageLanguage).Module.ModuleHandle);
        InlineValidator<Probe> validator = new() { rules => rules.RuleFor(probe => probe.Text).NotEmpty() };
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;

        // Act
        ValidationResult result;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(machineCulture);
            result = validator.Validate(new Probe(string.Empty));
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalUiCulture;
        }

        // Assert
        result.Errors.ShouldHaveSingleItem().ErrorMessage.ShouldBe("'Text' must not be empty.");
    }

    private sealed record Probe(string Text);
}
