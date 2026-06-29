using FluentValidation.Results;
using LotroKoniecDev.AuthSystem.Infrastructure.Emails;
using Shouldly;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Settings;

/// <summary>
/// Pure unit coverage for the AuthSystem email/SMTP startup guard (M6-05), focused on the SMTP host
/// the ticket calls out. It lives in the integration project only because the AuthSystem has no API
/// unit project; it instantiates no factory and starts no container.
/// </summary>
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
