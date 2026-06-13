using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration;

#pragma warning disable CA1515
// ReSharper disable once ClassNeverInstantiated.Global
public class TranslationSystemApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
#pragma warning restore CA1515
{
    public const string TestIssuer = "https://localhost:5003";
    public const string TestAudience = "lotrokoniecdev-api";

    private static readonly SymmetricSecurityKey TestSigningKey =
        new("integration-test-signing-key-32-bytes!!"u8.ToArray());

    private readonly PostgreSqlContainer _postgresContainer =
        new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("LotroKoniecDevTranslation")
            .Build();

    private string _connectionString = string.Empty;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "ConnectionStrings:TranslationDatabase", _connectionString },
                { "Auth:Issuer", TestIssuer },
                { "Auth:Audience", TestAudience },
            });
        });

        builder.ConfigureTestServices(services =>
        {
            // The AuthSystem issuer is not running in these tests — validate tokens against
            // a local symmetric key instead of fetching signing keys from the JWKS endpoint.
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.ConfigurationManager = null;
                options.TokenValidationParameters.IssuerSigningKey = TestSigningKey;
            });
        });

        builder.ConfigureLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Warning);
        });
    }

    public static string CreateAccessToken(
        string role = AuthConstants.Roles.Translator,
        string scope = AuthConstants.Scopes.Api)
        => CreateToken(TestSigningKey, DateTime.UtcNow.AddMinutes(30), role, scope);

    public static string CreateExpiredAccessToken()
        => CreateToken(TestSigningKey, DateTime.UtcNow.AddMinutes(-20));

    public static string CreateTokenSignedWithUnknownKey()
        => CreateToken(
            new SymmetricSecurityKey("some-other-unknown-key-32-bytes-long!!!!"u8.ToArray()),
            DateTime.UtcNow.AddMinutes(30));

    private static string CreateToken(
        SymmetricSecurityKey signingKey,
        DateTime expires,
        string role = AuthConstants.Roles.Translator,
        string scope = AuthConstants.Scopes.Api)
    {
        JsonWebTokenHandler handler = new();

        SecurityTokenDescriptor descriptor = new()
        {
            Issuer = TestIssuer,
            Audience = TestAudience,
            IssuedAt = expires.AddMinutes(-30),
            NotBefore = expires.AddMinutes(-30),
            Expires = expires,
            SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = Guid.NewGuid().ToString(),
                ["name"] = "integration-test-user",
                ["email"] = "translator@lotro.koniec.dev",
                ["role"] = role,
                ["scope"] = scope
            }
        };

        return handler.CreateToken(descriptor);
    }

    public async Task InitializeAsync()
    {
        await _postgresContainer.StartAsync();
        _connectionString = _postgresContainer.GetConnectionString();

        // Accessing Services triggers host startup; the compose migrator owns migrations in
        // deployed environments, so tests apply them explicitly against the container.
        using IServiceScope scope = Services.CreateScope();
        ApplicationWriteDbContext writeDbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        await writeDbContext.Database.MigrateAsync();
    }

    public new async Task DisposeAsync()
    {
        await _postgresContainer.DisposeAsync();
    }
}
