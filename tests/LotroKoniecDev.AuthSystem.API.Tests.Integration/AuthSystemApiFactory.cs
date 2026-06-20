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
                { "AdminUser:Username", "seeded-admin" },
                { "AdminUser:Email", "admin@lotro.koniec.dev" },
                { "AdminUser:Password", "AdminTest123!" },
                // Email identity is no longer baked into base appsettings.json (M6-06); supply it here
                // so the unconditional EmailOptionsValidator passes at startup (the senders themselves
                // are replaced with spies below, so these values are never used to send mail).
                { "Email:SenderEmail", "noreply@lotro.koniec.dev" },
                { "Email:Sender", "lotro.koniec.dev" },
                { "Email:Host", "localhost" },
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
