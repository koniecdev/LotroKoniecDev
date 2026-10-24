using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Testcontainers.PostgreSql;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.Tests.Shared;
using LotroKoniecDev.TranslationSystem.API.Features.Progress;
using LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;
using LotroKoniecDev.TranslationSystem.API.Features.Translations;
using LotroKoniecDev.TranslationSystem.API.Messaging;
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
        new PostgreSqlBuilder(PostgresImage.Name)
            .WithDatabase("LotroKoniecDevTranslation")
            .Build();

    private string _connectionString = string.Empty;

    /// <summary>
    /// Lets tests pin which columns a query reads, which the HTTP response cannot show (PERF-01). Tests
    /// call <see cref="SqlCommandRecorder.Clear"/> first; the collection runs one test at a time.
    /// </summary>
    public SqlCommandRecorder ReadContextSqlRecorder { get; } = new();

    /// <summary>
    /// Lets tests pin that a projection refresh is one UPDATE that never reads the old content (PERF-04).
    /// </summary>
    public SqlCommandRecorder WriteContextSqlRecorder { get; } = new();

    /// <summary>
    /// Each process removes copies it does not need, so two suites sharing a folder (two worktrees, say)
    /// would delete each other's files (ADR-0064).
    /// </summary>
    public string TranslationFileCopiesDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "lotro-translation-files-tests", Guid.NewGuid().ToString("N"));

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
                // Short, so the background rebuild converges fast (PERF-04).
                { "TranslationFileRebuild:DebounceWindow", "00:00:00.050" },
                { "TranslationFileDiskCache:Directory", TranslationFileCopiesDirectory },
                // No broker here, and the consumer is removed below; these values only satisfy the
                // validator. BrokeredTranslationSystemApiFactory points them at a real broker.
                { "RabbitMq:Host", "localhost" },
                { "RabbitMq:Port", "59998" },
                { "RabbitMq:Username", "rabbitmq" },
                { "RabbitMq:Password", "changeme" },
            });
        });

        builder.ConfigureTestServices(services =>
        {
            RemoveHostedService<AccountErasedConsumer>(services);

            // No AuthSystem here, so tokens are checked against a local symmetric key.
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.ConfigurationManager = null;
                options.TokenValidationParameters.IssuerSigningKey = TestSigningKey;
            });

            // Since EF Core 9 AddDbContext calls add up, so this adds the interceptors. The production
            // contexts are pooled, so their options are singletons; a scoped optionsLifetime would fail
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

    protected override IHost CreateHost(IHostBuilder builder) => HostStartOrder.BuildAndStart(builder);

    /// <summary>
    /// Waits for the background rebuild first, or a rebuild from the previous class would write an
    /// artifact right after the TRUNCATE (ADR-0021). It also clears the counter cache, which would
    /// survive the TRUNCATE (#354), and the disk copies of the translation file.
    /// </summary>
    public async Task ResetDatabaseAsync(string truncateSql)
    {
        await WaitForArtifactRebuildQuiesceAsync();

        HybridCache hybridCache = Services.GetRequiredService<HybridCache>();
        await hybridCache.RemoveAsync([GetPublicProgress.CounterCacheKey, GetTranslationStats.CounterCacheKey]);

        DeleteTranslationFileCopies();

        using IServiceScope scope = Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        await dbContext.Database.ExecuteSqlRawAsync(truncateSql);
    }

    public void DeleteTranslationFileCopies()
    {
        if (Directory.Exists(TranslationFileCopiesDirectory))
        {
            Directory.Delete(TranslationFileCopiesDirectory, recursive: true);
        }
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

    /// <summary>
    /// The website's token names the auth API next to this one (#1023).
    /// </summary>
    public static string CreateAccessTokenForAudiences(params string[] audiences)
        => CreateToken(TestSigningKey, DateTime.UtcNow.AddMinutes(30), audiences: audiences);

    /// <summary>
    /// Expired one minute ago: inside JwtBearer's default five-minute skew, so only the zero skew refuses
    /// it (#933).
    /// </summary>
    public static string CreateExpiredAccessToken()
        => CreateAccessTokenExpiringAt(DateTime.UtcNow.AddMinutes(-1));

    public static string CreateAccessTokenExpiringAt(DateTime expires)
        => CreateToken(TestSigningKey, expires);

    public static string CreateAccessTokenOfType(string tokenType)
        => CreateToken(TestSigningKey, DateTime.UtcNow.AddMinutes(30), tokenType: tokenType);

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
        string email = TestUserEmail,
        string[]? audiences = null,
        string tokenType = AuthConstants.TokenTypes.AccessToken)
    {
        JsonWebTokenHandler handler = new();

        SecurityTokenDescriptor descriptor = new()
        {
            TokenType = tokenType,
            Issuer = TestIssuer,
            // One audience goes out as a string, like the service token and every real one-API token.
            Audience = audiences is null ? TestAudience : null,
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

        foreach (string audience in audiences ?? [])
        {
            descriptor.Audiences.Add(audience);
        }

        return handler.CreateToken(descriptor);
    }

    /// <summary>
    /// Exactly one registration must exist, so a renamed or doubled one fails here instead of leaving a
    /// background service running in the suite.
    /// </summary>
    internal static void RemoveHostedService<THostedService>(IServiceCollection services)
        where THostedService : IHostedService
    {
        List<ServiceDescriptor> descriptors = services
            .Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(THostedService))
            .ToList();

        if (descriptors.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one hosted registration of {typeof(THostedService).Name}, found {descriptors.Count}.");
        }

        services.Remove(descriptors[0]);
    }

    public virtual async Task InitializeAsync()
    {
        await _postgresContainer.StartAsync();

        // So a PostgreSQL failure names the processes involved, not "Detail redacted" (#821).
        _connectionString = new NpgsqlConnectionStringBuilder(_postgresContainer.GetConnectionString())
        {
            IncludeErrorDetail = true
        }.ConnectionString;

        // N-1 runs apply the HEAD schema here, so MigrateAsync below does nothing (ADR-0024).
        await N1CompatSchemaSeam.ApplyIfConfiguredAsync(_postgresContainer, "translation.sql");

        // The compose migrator owns migrations when deployed, so tests apply them here.
        using IServiceScope scope = Services.CreateScope();
        ApplicationWriteDbContext writeDbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        await writeDbContext.Database.MigrateAsync();
    }

    public new virtual async Task DisposeAsync()
    {
        await _postgresContainer.DisposeAsync();
        DeleteTranslationFileCopies();
    }
}
