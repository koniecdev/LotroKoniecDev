using Microsoft.Extensions.Options;

namespace LotroKoniecDev.AuthSystem.API.Settings;

internal sealed class OpenIddictSettingsValidator(IWebHostEnvironment environment)
    : IValidateOptions<OpenIddictSettings>
{
    public ValidateOptionsResult Validate(string? name, OpenIddictSettings options)
    {
        // In development/testing, ephemeral keys are used - no validation needed
        if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
        {
            return ValidateOptionsResult.Success;
        }

        List<string> errors = [];

        if (string.IsNullOrWhiteSpace(options.EncryptionKey.Key))
        {
            errors.Add("EncryptionKey.Key must be set in production. Generate a 256-bit (32-byte) key and base64 encode it.");
        }

        if (string.IsNullOrWhiteSpace(options.SigningKey.RsaPrivateKeyXml))
        {
            errors.Add("SigningKey.RsaPrivateKeyXml must be set in production. Generate an RSA key pair and base64 encode the XML.");
        }

        if (string.IsNullOrWhiteSpace(options.ApiClientSecret))
        {
            errors.Add("ApiClientSecret must be set in production.");
        }
        else if (options.ApiClientSecret.Length < 32)
        {
            errors.Add("ApiClientSecret must be at least 32 characters for security.");
        }

        if (options.Issuer.Contains("localhost", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Issuer cannot contain 'localhost' in production.");
        }

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
