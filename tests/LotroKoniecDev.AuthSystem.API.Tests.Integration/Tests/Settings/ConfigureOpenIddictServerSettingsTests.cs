using System.Security.Cryptography;
using System.Text;
using LotroKoniecDev.AuthSystem.API.Extensions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Server;
using Shouldly;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Settings;

/// <summary>
/// Regression coverage for the production signing-key wiring in <see cref="ConfigureOpenIddictServerSettings"/>.
/// The RSA loaded from configuration must outlive <c>Configure</c>: OpenIddict keeps it for the app's
/// lifetime to sign tokens and to export the public key on /.well-known/jwks. Disposing it (a stray
/// <c>using</c>) made the JWKS endpoint throw <see cref="ObjectDisposedException"/>, which the prod-parity
/// stack (M6-07) surfaced as IDX20807 at every relying party. Pure test; it starts no container.
/// </summary>
public sealed class ConfigureOpenIddictServerSettingsTests
{
    private const string Production = "Production";

    [Fact]
    public void Configure_Production_LeavesCurrentSigningKeyUsableForJwksExport()
    {
        (string base64Xml, byte[] expectedPublicKey) = NewSigningKey();
        ConfigureOpenIddictServerSettings configurator = CreateConfigurator(base64Xml);
        OpenIddictServerOptions options = new();

        configurator.Configure(options);

        RsaSecurityKey signingKey = SigningKeyWithId(options, "signing-key-current");
        // JWKS exports the public key from this RSA. Against the old `using` (disposed) code the export
        // throws ObjectDisposedException — but only on Linux/CI (RSAOpenSsl frees the native key on
        // Dispose); macOS's RSASecurityTransforms tolerates use-after-dispose, so off-Linux it is the
        // key-material equality below that proves the key actually survived Configure().
        byte[] exportedPublicKey = signingKey.Rsa!.ExportSubjectPublicKeyInfo();
        exportedPublicKey.ShouldBe(expectedPublicKey);
    }

    [Fact]
    public void Configure_ProductionWithKeyRotation_LeavesPreviousSigningKeyUsableForJwksExport()
    {
        (string currentBase64Xml, _) = NewSigningKey();
        (string previousBase64Xml, byte[] expectedPreviousPublicKey) = NewSigningKey();
        ConfigureOpenIddictServerSettings configurator = CreateConfigurator(currentBase64Xml, previousBase64Xml);
        OpenIddictServerOptions options = new();

        configurator.Configure(options);

        RsaSecurityKey previousSigningKey = SigningKeyWithId(options, "signing-key-previous");
        // Same Linux-only disposal caveat as the current-key test; equality is the cross-platform guard.
        byte[] exportedPublicKey = previousSigningKey.Rsa!.ExportSubjectPublicKeyInfo();
        exportedPublicKey.ShouldBe(expectedPreviousPublicKey);
    }

    private static RsaSecurityKey SigningKeyWithId(OpenIddictServerOptions options, string keyId)
        => options.SigningCredentials
            .Select(credential => credential.Key)
            .OfType<RsaSecurityKey>()
            .Single(key => key.KeyId == keyId);

    private static (string Base64Xml, byte[] PublicKeyInfo) NewSigningKey()
    {
        using RSA rsa = RSA.Create(2048);
        byte[] publicKeyInfo = rsa.ExportSubjectPublicKeyInfo();
        string base64Xml = Convert.ToBase64String(Encoding.UTF8.GetBytes(rsa.ToXmlString(includePrivateParameters: true)));
        return (base64Xml, publicKeyInfo);
    }

    private static ConfigureOpenIddictServerSettings CreateConfigurator(
        string signingKeyXml,
        string? previousSigningKeyXml = null)
    {
        Dictionary<string, string?> values = new()
        {
            ["OpenIddict:Issuer"] = "https://auth.lotro.test",
            ["OpenIddict:EncryptionKey:Key"] = Convert.ToBase64String(new byte[32]),
            ["OpenIddict:SigningKey:RsaPrivateKeyXml"] = signingKeyXml
        };

        if (previousSigningKeyXml is not null)
        {
            values["OpenIddict:SigningKey:PreviousRsaPrivateKeyXml"] = previousSigningKeyXml;
        }

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        return new ConfigureOpenIddictServerSettings(configuration, new FakeWebHostEnvironment(Production));
    }
}

file sealed class FakeWebHostEnvironment : IWebHostEnvironment
{
    public FakeWebHostEnvironment(string environmentName)
    {
        EnvironmentName = environmentName;
    }

    public string EnvironmentName { get; set; }
    public string ApplicationName { get; set; } = "auth-tests";
    public string ContentRootPath { get; set; } = string.Empty;
    public IFileProvider ContentRootFileProvider { get; set; } = null!;
    public string WebRootPath { get; set; } = string.Empty;
    public IFileProvider WebRootFileProvider { get; set; } = null!;
}
