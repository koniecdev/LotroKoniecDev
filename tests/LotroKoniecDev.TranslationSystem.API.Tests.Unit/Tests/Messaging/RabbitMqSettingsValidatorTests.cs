using FluentValidation.Results;
using LotroKoniecDev.TranslationSystem.API.Messaging;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Messaging;

public sealed class RabbitMqSettingsValidatorTests
{
    private readonly RabbitMqSettingsValidator _validator = new();

    [Fact]
    public void Validate_WithCompleteValidSettings_Passes()
    {
        ValidationResult result = _validator.Validate(Settings());

        result.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WithMissingHost_FailsNamingTheKey(string? host)
    {
        ValidationResult result = _validator.Validate(Settings(host: host!));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.ErrorMessage.Contains("RabbitMq:Host", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Validate_WithPortOutOfRange_FailsNamingTheKey(int port)
    {
        ValidationResult result = _validator.Validate(Settings(port: port));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.ErrorMessage.Contains("RabbitMq:Port", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(65535)]
    public void Validate_WithPortAtTheEdgeOfTheRange_Passes(int port)
    {
        ValidationResult result = _validator.Validate(Settings(port: port));

        result.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Validate_WithMissingUsername_FailsNamingTheKey(string? username)
    {
        ValidationResult result = _validator.Validate(Settings(username: username!));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.ErrorMessage.Contains("RabbitMq:Username", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Validate_WithMissingPassword_FailsNamingTheKey(string? password)
    {
        ValidationResult result = _validator.Validate(Settings(password: password!));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.ErrorMessage.Contains("RabbitMq:Password", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WithEmptyVirtualHost_FailsNamingTheKey()
    {
        ValidationResult result = _validator.Validate(Settings(virtualHost: ""));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.ErrorMessage.Contains("RabbitMq:VirtualHost", StringComparison.Ordinal));
    }

    private static RabbitMqSettings Settings(
        string host = "rabbitmq",
        int port = 5672,
        string username = "rabbitmq",
        string password = "a-broker-password",
        string virtualHost = "/") =>
        new()
        {
            Host = host,
            Port = port,
            Username = username,
            Password = password,
            VirtualHost = virtualHost
        };
}
