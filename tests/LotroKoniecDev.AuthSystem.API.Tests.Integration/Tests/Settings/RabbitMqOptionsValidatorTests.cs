using FluentValidation.Results;
using LotroKoniecDev.AuthSystem.Infrastructure.Messaging;
using Shouldly;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Settings;

/// <summary>
/// Pure unit coverage for the AuthSystem broker startup guard. It lives in the integration project only
/// because the AuthSystem has no API unit project; it instantiates no factory and starts no container.
/// </summary>
public sealed class RabbitMqOptionsValidatorTests
{
    private readonly RabbitMqOptionsValidator _validator = new();

    [Fact]
    public void Validate_WithCompleteValidOptions_Passes()
    {
        RabbitMqOptions options = Options();

        ValidationResult result = _validator.Validate(options);

        result.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WithMissingHost_FailsNamingTheKey(string? host)
    {
        RabbitMqOptions options = Options(host: host!);

        ValidationResult result = _validator.Validate(options);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.PropertyName == nameof(RabbitMqOptions.Host));
        result.Errors.ShouldContain(error =>
            error.ErrorMessage.Contains("RabbitMq:Host", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Validate_WithPortOutsideTheValidRange_Fails(int port)
    {
        RabbitMqOptions options = Options(port: port);

        ValidationResult result = _validator.Validate(options);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.PropertyName == nameof(RabbitMqOptions.Port));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WithMissingUsername_Fails(string? username)
    {
        RabbitMqOptions options = Options(username: username!);

        ValidationResult result = _validator.Validate(options);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.PropertyName == nameof(RabbitMqOptions.Username));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WithMissingPassword_Fails(string? password)
    {
        RabbitMqOptions options = Options(password: password!);

        ValidationResult result = _validator.Validate(options);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.PropertyName == nameof(RabbitMqOptions.Password));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WithMissingVirtualHost_Fails(string? virtualHost)
    {
        RabbitMqOptions options = Options(virtualHost: virtualHost!);

        ValidationResult result = _validator.Validate(options);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.PropertyName == nameof(RabbitMqOptions.VirtualHost));
    }

    private static RabbitMqOptions Options(
        string host = "localhost",
        int port = 5672,
        string username = "rabbitmq",
        string password = "changeme",
        string virtualHost = "/") => new()
    {
        Host = host,
        Port = port,
        Username = username,
        Password = password,
        VirtualHost = virtualHost
    };
}
