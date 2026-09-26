using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;
using LotroKoniecDev.AuthSystem.API.BackgroundServices;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Services.Emails;
using LotroKoniecDev.AuthSystem.API.Services.Maintenance;
using LotroKoniecDev.AuthSystem.API.Services.ResponseTiming;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Password;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
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

    /// <summary>
    /// Sits on every <see cref="AuthDbContext"/> this host builds and does nothing until a test arms it.
    /// Shared per factory; <see cref="Shared.Bases.AsyncLifetimeTestBase"/> disarms it before and after
    /// each test.
    /// </summary>
    public DbCommandFailureInjector DbCommandFailures { get; } = new();

    /// <inheritdoc cref="DbCommandFailures"/>
    public DbCommitFailureInjector DbCommitFailures { get; } = new();

    private WebApplicationFactory<Program>? _responseTimeFloorHost;

    /// <summary>
    /// This host with the production response-time floor in place of <see cref="NoResponseTimeFloor"/>
    /// (ADR-0059). It is built once and shared, because every request to it waits for the floor anyway.
    /// It runs no outbox relay: a second relay on this database could take a row that another test waits
    /// for through the main host's spies. Not thread-safe: its callers share the one sequential "AuthApi"
    /// collection.
    /// </summary>
    public async Task<WebApplicationFactory<Program>> GetResponseTimeFloorHostAsync()
    {
        if (_responseTimeFloorHost is not null)
        {
            return _responseTimeFloorHost;
        }

        WebApplicationFactory<Program> host = WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                RemoveHostedService<OutboxRelay>(services);
                ReplaceSingleton<IResponseTimeFloor>(services, new ResponseTimeFloor(TimeProvider.System));
            }));

        // The first request builds the host, and the first account lookup compiles the EF query and warms
        // Identity, which together can take longer than a floor. Without this a member that forgot to wait
        // could still pass its first floor test. The host is kept only once it answered, so a failed
        // warm-up is not handed to the next test.
        try
        {
            using HttpClient client = host.CreateClient();
            using HttpResponseMessage response = await client.PostAsJsonAsync(
                new Uri("auth/forgot-password", UriKind.Relative),
                new ForgotPasswordRequest("warm-up@example.com"));
            response.EnsureSuccessStatusCode();
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }

        _responseTimeFloorHost = host;
        return host;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "ConnectionStrings:AuthDatabase", _connectionString },
                { "OpenIddict:Issuer", "https://localhost:5002" },
                // The token lifetimes are deliberately NOT pinned here. They come from the shipped
                // appsettings.json, so a test host can never keep running an old window after
                // production moved to a new one (#686).
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

            ReplaceSingleton<IResponseTimeFloor>(services, new NoResponseTimeFloor());

            services.AddSingleton<SpyPasswordHasher>();
            services.AddSingleton<IPasswordHasher<ApplicationUser>>(sp =>
                sp.GetRequiredService<SpyPasswordHasher>());

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

            // Replace the e-mail change sender with a spy, for capturing the confirmation and revert
            // tokens in tests
            ServiceDescriptor? existingEmailChangeSender = services
                .FirstOrDefault(d => d.ServiceType == typeof(IEmailChangeEmailSender));
            if (existingEmailChangeSender is not null)
            {
                services.Remove(existingEmailChangeSender);
            }

            services.AddSingleton<SpyEmailChangeEmailSender>();
            services.AddSingleton<IEmailChangeEmailSender>(sp =>
                sp.GetRequiredService<SpyEmailChangeEmailSender>());

            // This suite runs without a broker. The consumer would keep retrying the connection and
            // filling the log with warnings, so it is removed. Its logic has its own unit tests through
            // EmailConfirmationRequestProcessor.
            RemoveHostedService<EmailDispatchConsumer>(services);

            // No job that runs on the real clock is hosted here. A prune pass and a due deletion each
            // write several tables in one transaction, and the cleaner's TRUNCATE takes the same tables
            // in another order: the prune's one-minute start deadlocked it mid-suite (#821). Their tests
            // call PruneOnceAsync and IAccountDeletionFinalizer directly. The outbox relay stays: it
            // wakes on a signal, registration tests need it, and each of its statements touches one table.
            RemoveHostedService<OpenIddictPruneService>(services);
            RemoveHostedService<AccountDeletionFinalizerHostedService>(services);
            services.AddSingleton<OpenIddictPruneService>();

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
                options.AddInterceptors(DbCommandFailures, DbCommitFailures);
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

        // With the error detail on, a PostgreSQL failure such as a deadlock (40P01) reports which
        // processes held what, instead of "Detail redacted" (#821). The test database holds no data
        // worth protecting from a test log.
        _connectionString = new NpgsqlConnectionStringBuilder(_postgresContainer.GetConnectionString())
        {
            IncludeErrorDetail = true
        }.ConnectionString;

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
        if (_responseTimeFloorHost is not null)
        {
            await _responseTimeFloorHost.DisposeAsync();
        }

        await _postgresContainer.DisposeAsync();
    }

    /// <summary>
    /// Swaps one production singleton for a test one. Strict for the same reason as
    /// <see cref="RemoveHostedService{THostedService}"/>: exactly one registration must exist.
    /// </summary>
    private static void ReplaceSingleton<TService>(IServiceCollection services, TService replacement)
        where TService : class
    {
        List<ServiceDescriptor> descriptors = services
            .Where(d => d.ServiceType == typeof(TService))
            .ToList();

        if (descriptors.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one registration of {typeof(TService).Name}, found {descriptors.Count}. "
                + "AddAuthApi and this test host have drifted apart.");
        }

        services.Remove(descriptors[0]);
        services.AddSingleton(replacement);
    }

    /// <summary>
    /// Takes one hosted service off this host. Strict on purpose: exactly one registration must exist,
    /// so a production wiring that changes shape fails the whole suite instead of leaving a job on the
    /// clock or a removed one untested (#821).
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
                $"Expected exactly one hosted registration of {typeof(THostedService).Name}, found {descriptors.Count}. "
                + "AddAuthApi and this test host have drifted apart (#821).");
        }

        services.Remove(descriptors[0]);
    }
}
