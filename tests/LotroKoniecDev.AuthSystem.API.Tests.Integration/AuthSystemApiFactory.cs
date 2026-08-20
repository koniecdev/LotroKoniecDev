using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;
using LotroKoniecDev.AuthSystem.API.BackgroundServices;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Services.Emails;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.Infrastructure.Messaging;
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

    /// <summary>
    /// The origin of the web client this host is configured with. It is also the frontend origin the
    /// login page falls back to when a sign-in has nowhere to continue, so tests compare against it
    /// instead of repeating the string.
    /// </summary>
    public const string TestFrontendAppRoot = "https://localhost:5001";

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
                { "OpenIddict:WebClient:RedirectUris:0", TestFrontendAppRoot + "/callback" },
                { "OpenIddict:WebClient:PostLogoutRedirectUris:0", TestFrontendAppRoot },
                { "AdminUser:Username", "seededadmin" },
                { "AdminUser:Email", "admin@lotro-translator.pl" },
                { "AdminUser:Password", "AdminTest123!" },
                // The e-mail settings are no longer in the base appsettings.json (M6-06), so they are
                // set here to satisfy EmailOptionsValidator at startup. The senders are replaced with
                // spies below, so these values never send anything.
                // The port is one nothing listens on, so SmtpHealthCheck is always Unhealthy. The full
                // /health test must not change its answer when a local mailpit runs on :1025.
                { "Email:SenderEmail", "noreply@lotro-translator.pl" },
                { "Email:Sender", "lotro-translator.pl" },
                { "Email:Host", "localhost" },
                { "Email:Port", "59999" },
                // This suite has no broker. These values only have to satisfy RabbitMqOptionsValidator
                // at startup. The port is one nothing listens on, the same trick as Email:Port above, so
                // RabbitMqHealthCheck is always Unhealthy. The full /health test must not change its
                // answer when the dev compose broker runs on :5672.
                { "RabbitMq:Host", "localhost" },
                { "RabbitMq:Port", "59998" },
                { "RabbitMq:Username", "rabbitmq" },
                { "RabbitMq:Password", "changeme" },
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

            // This suite runs without a broker. The consumer would keep retrying the connection and
            // filling the log with warnings, so it is removed. Its logic has its own unit tests through
            // EmailConfirmationRequestProcessor.
            ServiceDescriptor? emailConsumer = services.FirstOrDefault(d =>
                d.ServiceType == typeof(IHostedService)
                && d.ImplementationType == typeof(EmailDispatchConsumer));
            if (emailConsumer is not null)
            {
                services.Remove(emailConsumer);
            }

            // The RabbitMQ publisher is replaced with a spy, because this suite has no broker and the
            // outbox relay tests check what was published. The spy also plays the part of the removed
            // consumer, so registering a user still ends in a captured confirmation e-mail:
            // outbox, relay, spy publish, processor, spy sender.
            ServiceDescriptor? existingMessagePublisher = services
                .FirstOrDefault(d => d.ServiceType == typeof(IMessagePublisher));
            if (existingMessagePublisher is not null)
            {
                services.Remove(existingMessagePublisher);
            }

            services.AddSingleton<SpyMessagePublisher>(sp =>
                new SpyMessagePublisher(message => DeliverLikeTheConsumerWouldAsync(sp, message)));
            services.AddSingleton<IMessagePublisher>(sp =>
                sp.GetRequiredService<SpyMessagePublisher>());

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

    /// <summary>
    /// This suite's stand-in for the step from broker to consumer. What the relay publishes goes
    /// through the same <see cref="IEmailMessageProcessor"/> the registry selects and the same
    /// <see cref="EmailDeliveryProcessor"/> the real consumer uses, in a new scope per message, exactly
    /// as <c>EmailDispatchConsumer.OnDeliveredAsync</c> does.
    /// That includes choosing the processor by message type and never by routing key (ADR-0038), and
    /// the duplicate check through the inbox (ADR-0037), so both run against this suite's real
    /// PostgreSQL.
    /// </summary>
    private static async Task DeliverLikeTheConsumerWouldAsync(
        IServiceProvider services,
        SpyMessagePublisher.PublishedMessage message)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();

        IEmailMessageProcessor? processor =
            scope.ServiceProvider.GetKeyedService<IEmailMessageProcessor>(message.Type);
        if (processor is null)
        {
            return;
        }

        object? payload = processor.TryDeserialize(System.Text.Encoding.UTF8.GetBytes(message.Payload));
        if (payload is null)
        {
            return;
        }

        EmailDeliveryProcessor deliveryProcessor =
            scope.ServiceProvider.GetRequiredService<EmailDeliveryProcessor>();
        await deliveryProcessor.ProcessOnceAsync(processor, payload, message.MessageId, CancellationToken.None);
    }

    public virtual async Task InitializeAsync()
    {
        await _postgresContainer.StartAsync();
        _connectionString = _postgresContainer.GetConnectionString();

        // N-1 compat runs (ADR-0024) pre-apply the HEAD schema here; the seeder's MigrateAsync
        // then no-ops and this suite exercises its (older) code against the newer schema.
        await N1CompatSchemaSeam.ApplyIfConfiguredAsync(_postgresContainer, "auth.sql");

        // Reading Services starts the host. The seeder is skipped in the Testing environment, so we
        // seed here using the test host's own services, which have the right connection string.
        IWebHostEnvironment environment = Services.GetRequiredService<IWebHostEnvironment>();
        await DatabaseSeederExtensions.SeedAuthDatabaseAsync(Services, environment);
    }

    public new virtual async Task DisposeAsync()
    {
        await _postgresContainer.DisposeAsync();
    }
}
