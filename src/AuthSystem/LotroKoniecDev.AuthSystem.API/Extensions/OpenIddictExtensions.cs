using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Server;
using LotroKoniecDev.AuthSystem.API.Settings;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.SharedKernel.Authorization;

namespace LotroKoniecDev.AuthSystem.API.Extensions;

internal static class OpenIddictExtensions
{
    public static IServiceCollection AddOpenIddictServer(
        this IServiceCollection services,
        IWebHostEnvironment environment)
    {
        services.AddOpenIddict()
            .AddCore(options =>
            {
                options.UseEntityFrameworkCore()
                       .UseDbContext<AuthDbContext>();
            })
            .AddServer(options =>
            {
                options
                    .SetTokenEndpointUris("connect/token")
                    .SetAuthorizationEndpointUris("connect/authorize")
                    .SetUserInfoEndpointUris("connect/userinfo")
                    .SetIntrospectionEndpointUris("connect/introspect")
                    .SetRevocationEndpointUris("connect/revoke")
                    .SetEndSessionEndpointUris("connect/logout");

                options
                    .AllowRefreshTokenFlow()
                    .AllowClientCredentialsFlow()
                    .AllowAuthorizationCodeFlow();

                // Password flow is only enabled in testing for integration/E2E tests.
                // In production, use authorization code flow with PKCE.
                if (environment.IsEnvironment("Testing"))
                {
                    options.AllowPasswordFlow();
                }

                options.RequireProofKeyForCodeExchange();

                // Enable rolling refresh tokens - when a refresh token is used, it's automatically
                // invalidated and a new one is issued. This prevents token replay attacks.
                // Reference tokens are stored in the database, allowing revocation.
                options.UseReferenceRefreshTokens();

                options.RegisterScopes(
                    OpenIddict.Abstractions.OpenIddictConstants.Scopes.OpenId,
                    OpenIddict.Abstractions.OpenIddictConstants.Scopes.Email,
                    OpenIddict.Abstractions.OpenIddictConstants.Scopes.Profile,
                    OpenIddict.Abstractions.OpenIddictConstants.Scopes.Roles,
                    OpenIddict.Abstractions.OpenIddictConstants.Scopes.OfflineAccess,
                    AuthConstants.Scopes.Api,
                    AuthConstants.Scopes.Service);

                // Disable access token encryption to allow standard JWT Bearer validation.
                // Access tokens are signed (tamper-proof) but not encrypted.
                // This is a common pattern - encryption is optional in OAuth2/OIDC.
                options.DisableAccessTokenEncryption();

                // In development/testing, use ephemeral keys.
                // In production, keys are configured via ConfigureOpenIddictServerSettings.
                if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
                {
                    options.AddEphemeralSigningKey()
                        .AddEphemeralEncryptionKey();
                }

                OpenIddictServerAspNetCoreBuilder aspNetCoreBuilder = options.UseAspNetCore()
                    .EnableTokenEndpointPassthrough()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableUserInfoEndpointPassthrough()
                    .EnableEndSessionEndpointPassthrough();

                if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
                {
                    aspNetCoreBuilder.DisableTransportSecurityRequirement();
                }
            })
            .AddValidation(options =>
            {
                options.UseLocalServer();
                options.UseAspNetCore();
            });

        services.AddSingleton<IConfigureOptions<OpenIddictServerOptions>, ConfigureOpenIddictServerSettings>();

        return services;
    }
}

internal sealed class ConfigureOpenIddictServerSettings(IConfiguration configuration, IWebHostEnvironment environment)
    : IConfigureOptions<OpenIddictServerOptions>
{
    public void Configure(OpenIddictServerOptions options)
    {
        OpenIddictSettings settings = configuration
            .GetSection(OpenIddictSettings.ConfigurationSection)
            .Get<OpenIddictSettings>()
            ?? throw new InvalidOperationException("OpenIddict settings are not configured");

        options.Issuer = new Uri(settings.Issuer);
        options.AccessTokenLifetime = TimeSpan.FromMinutes(settings.AccessTokenLifetimeMinutes);
        options.RefreshTokenLifetime = TimeSpan.FromDays(settings.RefreshTokenLifetimeDays);

        // In development/testing, use ephemeral keys only (configured in AddServer).
        // In production, use RSA asymmetric keys - public key is exposed via JWKS for token validation.
        if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
        {
            return;
        }

        try
        {
            // Encryption key (symmetric) - used for authorization codes, refresh tokens, etc.
            // Not exposed via JWKS, so symmetric is fine here.
            byte[] encKeyBytes = Convert.FromBase64String(settings.EncryptionKey.Key);
            if (encKeyBytes.Length < 32)
            {
                throw new InvalidOperationException(
                    "Encryption key must be at least 256 bits (32 bytes).");
            }

            SymmetricSecurityKey encryptionKey = new(encKeyBytes);
            options.EncryptionCredentials.Add(new EncryptingCredentials(
                encryptionKey, SecurityAlgorithms.Aes256KW, SecurityAlgorithms.Aes256CbcHmacSha512));
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                "Invalid encryption key format. The key must be a valid base64-encoded string.", ex);
        }

        try
        {
            // Signing key (RSA asymmetric) - public key exposed via JWKS for resource servers.
            string rsaXml = System.Text.Encoding.UTF8.GetString(
                Convert.FromBase64String(settings.SigningKey.RsaPrivateKeyXml));

            // Not wrapped in `using`: OpenIddict retains this RSA (via RsaSecurityKey in the singleton
            // server options) for the app's lifetime — to sign tokens AND to export the public key on
            // /.well-known/jwks. Disposing it makes the JWKS endpoint throw ObjectDisposedException
            // (RSAOpenSsl), so discovery succeeds but JWKS 500s → IDX20807 at every relying party.
            RSA rsa = RSA.Create();
            rsa.FromXmlString(rsaXml);

            // Validate minimum key size (RSA-2048)
            if (rsa.KeySize < 2048)
            {
                throw new InvalidOperationException(
                    $"RSA signing key must be at least 2048 bits. Current key size: {rsa.KeySize} bits.");
            }

            RsaSecurityKey rsaKey = new(rsa) { KeyId = "signing-key-current" };
            options.SigningCredentials.Add(new SigningCredentials(
                rsaKey, SecurityAlgorithms.RsaSha256));
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                "Invalid RSA signing key format. The key must be a valid base64-encoded XML string.", ex);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                "Invalid RSA signing key. The XML does not represent a valid RSA key.", ex);
        }

        // Register previous signing key for key rotation.
        // Existing tokens signed with the old key remain valid during the rotation window.
        if (string.IsNullOrWhiteSpace(settings.SigningKey.PreviousRsaPrivateKeyXml))
        {
            return;
        }

        try
        {
            string prevRsaXml = System.Text.Encoding.UTF8.GetString(
                Convert.FromBase64String(settings.SigningKey.PreviousRsaPrivateKeyXml));

            // Not disposed — same app-lifetime requirement as the current signing key above.
            RSA prevRsa = RSA.Create();
            prevRsa.FromXmlString(prevRsaXml);

            RsaSecurityKey prevRsaKey = new(prevRsa) { KeyId = "signing-key-previous" };
            options.SigningCredentials.Add(new SigningCredentials(
                prevRsaKey, SecurityAlgorithms.RsaSha256));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            throw new InvalidOperationException(
                "Invalid previous RSA signing key. Check SigningKey.PreviousRsaPrivateKeyXml format.", ex);
        }
    }
}
