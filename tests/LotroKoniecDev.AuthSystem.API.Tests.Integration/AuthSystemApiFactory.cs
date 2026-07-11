using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Services.Emails;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.Persistence;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration;

#pragma warning disable CA1515
// ReSharper disable once ClassNeverInstantiated.Global
public class AuthSystemApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
#pragma warning restore CA1515
{
    private readonly PostgreSqlContainer _postgresContainer =
        new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("LotroKoniecDevAuth")
            .Build();

    private string _connectionString = string.Empty;

    public const string TestApiClientSecret = "integration-test-secret-32-chars!";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "ConnectionStrings:AuthDatabase", _connectionString },
                { "OpenIddict:Issuer", "https://localhost:5002" },
                { "OpenIddict:AccessTokenLifetimeMinutes", "60" },
                { "OpenIddict:RefreshTokenLifetimeDays", "14" },
                { "OpenIddict:EncryptionKey:Key", "RGV2RW5jcnlwdGlvbktleTMyQnl0ZXNMb25nMTIzNDU=" },
                { "OpenIddict:SigningKey:Key", "RGV2U2lnbmluZ0tleTMyQnl0ZXNMb25nRW5vdWdoMTI=" },
                { "OpenIddict:ApiClientSecret", TestApiClientSecret },
                { "OpenIddict:WebClient:RedirectUris:0", "https://localhost:5001/callback" },
                { "OpenIddict:WebClient:PostLogoutRedirectUris:0", "https://localhost:5001" },
                { "AdminUser:Username", "seededadmin" },
                { "AdminUser:Email", "admin@lotro-translator.pl" },
                { "AdminUser:Password", "AdminTest123!" },
                // Email identity is no longer baked into base appsettings.json (M6-06); supply it here
                // so the unconditional EmailOptionsValidator passes at startup (the senders themselves
                // are replaced with spies below, so these values are never used to send mail).
                // The port is a deliberately dead one so SmtpHealthCheck is deterministically
                // Unhealthy — the full /health test must not flip when a local mailpit (:1025) runs.
                { "Email:SenderEmail", "noreply@lotro-translator.pl" },
                { "Email:Sender", "lotro-translator.pl" },
                { "Email:Host", "localhost" },
                { "Email:Port", "59999" },
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(this);
            services.AddScoped<CleanerService>();

            // Replace email sender with spy for capturing reset tokens in tests
            ServiceDescriptor? existingEmailSender = services
                .FirstOrDefault(d => d.ServiceType == typeof(IPasswordResetEmailSender));
            if (existingEmailSender is not null)
            {
                services.Remove(existingEmailSender);
            }

            services.AddSingleton<SpyPasswordResetEmailSender>();
            services.AddSingleton<IPasswordResetEmailSender>(sp =>
                sp.GetRequiredService<SpyPasswordResetEmailSender>());

            // Replace email confirmation sender with spy for capturing confirmation tokens in tests
            ServiceDescriptor? existingConfirmationSender = services
                .FirstOrDefault(d => d.ServiceType == typeof(IAccountConfirmationEmailSender));
            if (existingConfirmationSender is not null)
            {
                services.Remove(existingConfirmationSender);
            }

            services.AddSingleton<SpyAccountConfirmationEmailSender>();
            services.AddSingleton<IAccountConfirmationEmailSender>(sp =>
                sp.GetRequiredService<SpyAccountConfirmationEmailSender>());

            // Replace deletion email sender with spy for capturing cancel tokens in tests
            ServiceDescriptor? existingDeletionSender = services
                .FirstOrDefault(d => d.ServiceType == typeof(IAccountDeletionEmailSender));
            if (existingDeletionSender is not null)
            {
                services.Remove(existingDeletionSender);
            }

            services.AddSingleton<SpyAccountDeletionEmailSender>();
            services.AddSingleton<IAccountDeletionEmailSender>(sp =>
                sp.GetRequiredService<SpyAccountDeletionEmailSender>());

            // Replace AuthDbContext to use the test connection string directly
            ServiceDescriptor? dbContextDescriptor = services
                .FirstOrDefault(d => d.ServiceType == typeof(DbContextOptions<AuthDbContext>));
            if (dbContextDescriptor is not null)
            {
                services.Remove(dbContextDescriptor);
            }

            services.AddDbContext<AuthDbContext>(options =>
            {
                options.UseNpgsql(_connectionString, npgsqlOptions =>
                {
                    npgsqlOptions.EnableRetryOnFailure(
                        maxRetryCount: 3,
                        maxRetryDelay: TimeSpan.FromSeconds(10),
                        errorCodesToAdd: null);
                    npgsqlOptions.CommandTimeout(30);
                    npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", DatabaseSchemas.Auth);
                });
                options.UseOpenIddict();
            });
        });

        builder.ConfigureLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Warning);
        });
    }

    public async Task InitializeAsync()
    {
        await _postgresContainer.StartAsync();
        _connectionString = _postgresContainer.GetConnectionString();

        // N-1 compat runs (ADR-0024) pre-apply the HEAD schema here; the seeder's MigrateAsync
        // then no-ops and this suite exercises its (older) code against the newer schema.
        await N1CompatSchemaSeam.ApplyIfConfiguredAsync(_postgresContainer, "auth.sql");

        // Accessing Services triggers host startup.
        // The seeder is skipped in Testing environment, so we seed explicitly
        // using the test host's services (which have the correct connection string).
        IWebHostEnvironment environment = Services.GetRequiredService<IWebHostEnvironment>();
        await DatabaseSeederExtensions.SeedAuthDatabaseAsync(Services, environment);
    }

    public new async Task DisposeAsync()
    {
        await _postgresContainer.DisposeAsync();
    }
}
