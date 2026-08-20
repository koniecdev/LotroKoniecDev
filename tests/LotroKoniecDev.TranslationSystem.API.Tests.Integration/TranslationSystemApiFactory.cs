using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.TranslationSystem.API.Features.Progress;
using LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;
using LotroKoniecDev.TranslationSystem.API.Features.Translations;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.ReadDbContexts;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration;

#pragma warning disable CA1515
// ReSharper disable once ClassNeverInstantiated.Global
public class TranslationSystemApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
#pragma warning restore CA1515
{
    public const string TestIssuer = "https://localhost:5003";
    public const string TestAudience = "lotrokoniecdev-api";
    public const string TestUserDisplayName = "integration-test-user";
    public const string TestUserEmail = "translator@lotro-translator.pl";

    private static readonly SymmetricSecurityKey TestSigningKey =
        new("integration-test-signing-key-32-bytes!!"u8.ToArray());

    private readonly PostgreSqlContainer _postgresContainer =
        new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("LotroKoniecDevTranslation")
            .Build();

    private string _connectionString = string.Empty;

    /// <summary>
    /// Records the read context's SQL so tests can pin column-level fetch behavior the HTTP
    /// response cannot reveal (PERF-01/#286). Shared per factory; tests <see cref="SqlCommandRecorder.Clear"/>
    /// before the request under observation (the collection runs sequentially, so nothing interleaves).
    /// </summary>
    public SqlCommandRecorder ReadContextSqlRecorder { get; } = new();

    /// <summary>
    /// The same hook for the write context, so tests can pin how a projection refresh writes: one UPDATE
    /// in place that never reads the previous multi-MB content again (PERF-04, #289).
    /// </summary>
    public SqlCommandRecorder WriteContextSqlRecorder { get; } = new();

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
                // Short debounce so the background artifact rebuild (PERF-04) converges fast; the
                // polling assertions stay meaningful while the suite stays quick.
                { "TranslationFileRebuild:DebounceWindow", "00:00:00.050" },
            });
        });

        builder.ConfigureTestServices(services =>
        {
            // The AuthSystem is not running in these tests, so tokens are validated against a local
            // symmetric key instead of keys fetched from the JWKS endpoint.
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.ConfigurationManager = null;
                options.TokenValidationParameters.IssuerSigningKey = TestSigningKey;
            });

            // In EF Core 9 and later, several AddDbContext calls add up, so this adds the recording
            // interceptors to the two contexts instead of replacing their registrations.
            // The production contexts are pooled, which makes their options a singleton, so this extra
            // configuration has to be a singleton as well. The default scoped optionsLifetime fails
            // ValidateScopes (#572).
            services.AddDbContext<ApplicationReadDbContext>(
                options => options.AddInterceptors(ReadContextSqlRecorder),
                contextLifetime: ServiceLifetime.Scoped,
                optionsLifetime: ServiceLifetime.Singleton);
            services.AddDbContext<ApplicationWriteDbContext>(
                options => options.AddInterceptors(WriteContextSqlRecorder),
                contextLifetime: ServiceLifetime.Scoped,
                optionsLifetime: ServiceLifetime.Singleton);
        });

        builder.ConfigureLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Warning);
        });
    }

    /// <summary>
    /// Resets the shared database state between test classes: it waits for the background artifact
    /// rebuild to finish (PERF-04, ADR-0021) and then truncates the given tables.
    /// The wait is part of the reset and never something a caller has to remember, because a rebuild
    /// still running from the previous class would write an artifact row right after the TRUNCATE and
    /// break assertions such as the "no artifact yet" 404.
    /// Clearing the counter cache (AUDIT-EF-04, #354) is part of it for the same reason: a cached
    /// snapshot survives the TRUNCATE and would carry the previous test's counters into the next one.
    /// </summary>
    public async Task ResetDatabaseAsync(string truncateSql)
    {
        await WaitForArtifactRebuildQuiesceAsync();

        HybridCache hybridCache = Services.GetRequiredService<HybridCache>();
        await hybridCache.RemoveAsync([GetPublicProgress.CounterCacheKey, GetTranslationStats.CounterCacheKey]);

        using IServiceScope scope = Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        await dbContext.Database.ExecuteSqlRawAsync(truncateSql);
    }

    private async Task WaitForArtifactRebuildQuiesceAsync()
    {
        TranslationFileRebuildScheduler scheduler =
            Services.GetRequiredService<TranslationFileRebuildScheduler>();

        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (scheduler.PendingCount > 0)
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"Background artifact rebuild did not quiesce within 15 s; {scheduler.PendingCount} signal(s) still pending.");
            }

            await Task.Delay(10);
        }
    }

    public static string CreateAccessToken(
        string role = AuthConstants.Roles.Translator,
        string scope = AuthConstants.Scopes.Api,
        Guid? subject = null,
        string displayName = TestUserDisplayName,
        string email = TestUserEmail)
        => CreateToken(TestSigningKey, DateTime.UtcNow.AddMinutes(30), role, scope, subject, displayName, email);

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
        string scope = AuthConstants.Scopes.Api,
        Guid? subject = null,
        string displayName = TestUserDisplayName,
        string email = TestUserEmail)
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
                ["sub"] = (subject ?? Guid.NewGuid()).ToString(),
                ["name"] = displayName,
                ["email"] = email,
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

        // N-1 compat runs (ADR-0024) pre-apply the HEAD schema here; MigrateAsync below then
        // no-ops and this suite exercises its (older) code against the newer schema.
        await N1CompatSchemaSeam.ApplyIfConfiguredAsync(_postgresContainer, "translation.sql");

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
