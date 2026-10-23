using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using LotroKoniecDev.AuthSystem.API.Features.Auth;
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

                // Only for the integration and E2E tests.
                if (environment.IsEnvironment("Testing"))
                {
                    options.AllowPasswordFlow();
                }

                options.RequireProofKeyForCodeExchange();

                // Reference tokens live in the database, so they can be revoked. Sliding expiration stays
                // on, so the short lifetime never signs out a user who keeps working (#1014). Never call
                // DisableSlidingRefreshTokenExpiration.
                options.UseReferenceRefreshTokens();

                options.RegisterScopes(
                    OpenIddict.Abstractions.OpenIddictConstants.Scopes.OpenId,
                    OpenIddict.Abstractions.OpenIddictConstants.Scopes.Email,
                    OpenIddict.Abstractions.OpenIddictConstants.Scopes.Profile,
                    OpenIddict.Abstractions.OpenIddictConstants.Scopes.Roles,
                    OpenIddict.Abstractions.OpenIddictConstants.Scopes.OfflineAccess,
                    AuthConstants.Scopes.Api,
                    AuthConstants.Scopes.Service);

                // So standard JWT Bearer validation can read them. They are still signed.
                options.DisableAccessTokenEncryption();

                // Every other environment takes its keys from configuration in ConfigureOpenIddictServerSettings.
                if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
                {
                    options.AddEphemeralSigningKey()
                        .AddEphemeralEncryptionKey();
                }

                // EnableStatusCodePagesIntegration: a refused sign-in or sign-out link gets the app's error
                // page instead of OpenIddict's English plain text (#912).
                OpenIddictServerAspNetCoreBuilder aspNetCoreBuilder = options.UseAspNetCore()
                    .EnableTokenEndpointPassthrough()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableUserInfoEndpointPassthrough()
                    .EnableEndSessionEndpointPassthrough()
                    .EnableStatusCodePagesIntegration();

                if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
                {
                    aspNetCoreBuilder.DisableTransportSecurityRequirement();
                }

                // Introspection reads POST only: OpenIddict would also check a client secret sent by GET,
                // and a GET meets no rate limit (ADR-0061).
                options.RemoveEventHandler(
                    OpenIddictServerAspNetCoreHandlers.ExtractGetOrPostRequest<OpenIddictServerEvents.ExtractIntrospectionRequestContext>.Descriptor);
                options.AddEventHandler(
                    OpenIddictServerAspNetCoreHandlers.ExtractPostRequest<OpenIddictServerEvents.ExtractIntrospectionRequestContext>.Descriptor);

                // Log lines for the refusals OpenIddict makes itself (#977).
                options.AddEventHandler(OpenIddictTokenRefusals.NoteTokenUser.Descriptor);
                options.AddEventHandler(OpenIddictTokenRefusals.LogWhenRefused.Descriptor);
            })
            .AddValidation(options =>
            {
                options.UseLocalServer();
                options.UseAspNetCore();

                // Otherwise any token this server signed would get in, a service token for the translation
                // API too (#1023).
                options.AddAudiences(AuthConstants.Audiences.AuthApi);
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
        options.RefreshTokenLifetime = TimeSpan.FromMinutes(settings.RefreshTokenLifetimeMinutes);

        // Development and Testing use the throwaway keys set up in AddServer.
        if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
        {
            return;
        }

        try
        {
            // Symmetric is fine: it protects codes and refresh tokens and is never published through JWKS.
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
            string rsaXml = System.Text.Encoding.UTF8.GetString(
                Convert.FromBase64String(settings.SigningKey.RsaPrivateKeyXml));

            // Not inside a `using`: the singleton server options keep this RSA for signing and for JWKS.
            // Disposed, it makes JWKS return 500 and every client fail with IDX20807.
            RSA rsa = RSA.Create();
            rsa.FromXmlString(rsaXml);

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

        // Tokens signed with the previous key stay valid while both are registered (key rotation).
        if (string.IsNullOrWhiteSpace(settings.SigningKey.PreviousRsaPrivateKeyXml))
        {
            return;
        }

        try
        {
            string prevRsaXml = System.Text.Encoding.UTF8.GetString(
                Convert.FromBase64String(settings.SigningKey.PreviousRsaPrivateKeyXml));

            // Not disposed, for the same reason as the current key.
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
