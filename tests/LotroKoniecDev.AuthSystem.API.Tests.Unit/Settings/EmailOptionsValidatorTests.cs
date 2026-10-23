using FluentValidation.Results;
using LotroKoniecDev.AuthSystem.Infrastructure.Emails;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Settings;

public sealed class EmailOptionsValidatorTests
{
    private readonly EmailOptionsValidator _validator = new();

    [Fact]
    public void Validate_WithCompleteValidOptions_Passes()
    {
        EmailOptions options = Options();

        ValidationResult result = _validator.Validate(options);

        result.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WithMissingSmtpHost_FailsNamingTheKey(string? host)
    {
        EmailOptions options = Options(host: host!);

        ValidationResult result = _validator.Validate(options);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.PropertyName == nameof(EmailOptions.Host));
        result.Errors.ShouldContain(error =>
            error.ErrorMessage.Contains("Email:Host", StringComparison.Ordinal));
    }

    private static EmailOptions Options(string host = "mailpit") => new()
    {
        SenderEmail = "no-reply@lotro-translator.pl",
        Sender = "LotroKoniecDev",
        Host = host,
        Port = 587,
        Mode = EmailSecurityMode.StartTls
    };
}
