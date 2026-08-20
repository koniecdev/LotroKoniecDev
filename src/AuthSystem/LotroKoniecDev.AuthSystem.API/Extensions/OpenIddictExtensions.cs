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

                // The password flow is only on in Testing, for integration and E2E tests. Production
                // uses the authorization code flow with PKCE.
                if (environment.IsEnvironment("Testing"))
                {
                    options.AllowPasswordFlow();
                }

                options.RequireProofKeyForCodeExchange();

                // Rolling refresh tokens: using a refresh token invalidates it and issues a new one, so
                // an old token cannot be replayed. Reference tokens live in the database, which is what
                // makes revoking them possible.
                options.UseReferenceRefreshTokens();

                options.RegisterScopes(
                    OpenIddict.Abstractions.OpenIddictConstants.Scopes.OpenId,
                    OpenIddict.Abstractions.OpenIddictConstants.Scopes.Email,
                    OpenIddict.Abstractions.OpenIddictConstants.Scopes.Profile,
                    OpenIddict.Abstractions.OpenIddictConstants.Scopes.Roles,
                    OpenIddict.Abstractions.OpenIddictConstants.Scopes.OfflineAccess,
                    AuthConstants.Scopes.Api,
                    AuthConstants.Scopes.Service);

                // Access tokens are not encrypted, so standard JWT Bearer validation can read them.
                // They are still signed, so they cannot be changed. Encryption is optional in OAuth2
                // and OIDC, and leaving it off is common.
                options.DisableAccessTokenEncryption();

                // Development and testing use throwaway keys. In production the keys come from
                // ConfigureOpenIddictServerSettings.
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

        // Development and testing use throwaway keys only, set up in AddServer. Production uses RSA
        // keys, and the public one is published through JWKS so tokens can be validated.
        if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
        {
            return;
        }

        try
        {
            // The encryption key is symmetric and is used for authorization codes, refresh tokens and
            // the like. It is never published through JWKS, so a symmetric key is fine.
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
            // The signing key is RSA. Its public half is published through JWKS for resource servers.
            string rsaXml = System.Text.Encoding.UTF8.GetString(
                Convert.FromBase64String(settings.SigningKey.RsaPrivateKeyXml));

            // Not inside a `using`. OpenIddict keeps this RSA for the whole life of the app, through
            // an RsaSecurityKey in the singleton server options, both to sign tokens and to publish the
            // public key at /.well-known/jwks. Disposing it makes the JWKS endpoint throw
            // ObjectDisposedException from RSAOpenSsl, so discovery still works but JWKS returns 500
            // and every client fails with IDX20807.
            RSA rsa = RSA.Create();
            rsa.FromXmlString(rsaXml);

            // The key must be at least RSA-2048.
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

        // Register the previous signing key so keys can be rotated. Tokens signed with the old key stay
        // valid while both keys are registered.
        if (string.IsNullOrWhiteSpace(settings.SigningKey.PreviousRsaPrivateKeyXml))
        {
            return;
        }

        try
        {
            string prevRsaXml = System.Text.Encoding.UTF8.GetString(
                Convert.FromBase64String(settings.SigningKey.PreviousRsaPrivateKeyXml));

            // Not disposed, for the same reason as the current signing key above: it has to live as
            // long as the app.
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
