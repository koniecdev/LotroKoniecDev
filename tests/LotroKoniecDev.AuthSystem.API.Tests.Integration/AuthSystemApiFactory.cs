using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;
using LotroKoniecDev.AuthSystem.API.BackgroundServices;
using LotroKoniecDev.AuthSystem.API.Services.Emails;
using LotroKoniecDev.AuthSystem.API.Services.Maintenance;
using LotroKoniecDev.AuthSystem.API.Services.ResponseTiming;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Password;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Infrastructure.Messaging;
using LotroKoniecDev.AuthSystem.Infrastructure.PwnedPasswords;
using LotroKoniecDev.AuthSystem.Persistence;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.Tests.Shared;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration;

#pragma warning disable CA1515
// ReSharper disable once ClassNeverInstantiated.Global
public class AuthSystemApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
#pragma warning restore CA1515
{
    public const string TestApiClientSecret = "integration-test-secret-32-chars!";

    /// <summary>
    /// Also the frontend origin the login page falls back to, so tests compare against it instead of
    /// repeating the string.
    /// </summary>
    public const string TestFrontendAppRoot = "https://localhost:5001";

    private readonly PostgreSqlContainer _postgresContainer =
        new PostgreSqlBuilder(PostgresImage.Name)
            .WithDatabase("LotroKoniecDevAuth")
            .Build();

    private string _connectionString = string.Empty;

    private WebApplicationFactory<Program>? _responseTimeFloorHost;

    private WebApplicationFactory<Program>? _hostWithoutOutboxRelay;

    /// <summary>
    /// Sits on every <see cref="AuthDbContext"/> this host builds and does nothing until armed.
    /// <see cref="Shared.Bases.AsyncLifetimeTestBase"/> disarms it around each test.
    /// </summary>
    public DbCommandFailureInjector DbCommandFailures { get; } = new();

    /// <inheritdoc cref="DbCommandFailures"/>
    public DbCommitFailureInjector DbCommitFailures { get; } = new();

    /// <summary>
    /// Takes the place of the Have I Been Pwned client on this host and every host derived from it, so no
    /// test reaches the internet. Every password is clean until a test says otherwise.
    /// </summary>
    public StubPwnedPasswordChecker PwnedPasswords { get; } = new();

    /// <summary>
    /// This host with the production response-time floor (ADR-0059), built once and shared. It runs no outbox
    /// relay: a second relay on this database could take a row another test waits for. Not thread-safe: its
    /// callers share the sequential "AuthApi" collection.
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

        // The first request builds the host and warms EF and Identity, which can take longer than a
        // floor, so a member that forgot to wait could pass its first floor test. The host is kept only
        // once it answered.
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

    /// <summary>
    /// This host with no outbox relay, so a test can take the wake-up itself and see whether a save or a
    /// commit sent one (#781). It shares the database and the failure injectors, and has its own
    /// <c>OutboxSignal</c>. Not thread-safe, like the floor host.
    /// </summary>
    public WebApplicationFactory<Program> GetHostWithoutOutboxRelay()
    {
        _hostWithoutOutboxRelay ??= WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(RemoveHostedService<OutboxRelay>));

        return _hostWithoutOutboxRelay;
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
                // The token lifetimes are not pinned here. They come from the shipped appsettings.json,
                // so a test host never keeps an old window after production moved to a new one (#686).
                { "OpenIddict:EncryptionKey:Key", "RGV2RW5jcnlwdGlvbktleTMyQnl0ZXNMb25nMTIzNDU=" },
                { "OpenIddict:SigningKey:Key", "RGV2U2lnbmluZ0tleTMyQnl0ZXNMb25nRW5vdWdoMTI=" },
                { "OpenIddict:ApiClientSecret", TestApiClientSecret },
                { "OpenIddict:WebClient:RedirectUris:0", TestFrontendAppRoot + "/callback" },
                { "OpenIddict:WebClient:PostLogoutRedirectUris:0", TestFrontendAppRoot },
                { "AdminUser:Username", "seededadmin" },
                { "AdminUser:Email", "admin@lotro-translator.pl" },
                { "AdminUser:Password", "AdminTest123!" },
                // The base appsettings.json has no e-mail settings, so these satisfy EmailOptionsValidator
                // at startup. The senders are spies, so nothing is sent. Nothing listens on the port, so
                // SmtpHealthCheck is always Unhealthy, even when a local mailpit runs on :1025.
                { "Email:SenderEmail", "noreply@lotro-translator.pl" },
                { "Email:Sender", "lotro-translator.pl" },
                { "Email:Host", "localhost" },
                { "Email:Port", "59999" },
                // This suite has no broker. These values only satisfy RabbitMqOptionsValidator. Nothing
                // listens on the port, so RabbitMqHealthCheck is always Unhealthy, even when the dev
                // compose broker runs on :5672.
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
            ReplaceSingleton<IPwnedPasswordChecker>(services, PwnedPasswords);

            services.AddSingleton<SpyPasswordHasher>();
            services.AddSingleton<IPasswordHasher<ApplicationUser>>(sp =>
                sp.GetRequiredService<SpyPasswordHasher>());

            ServiceDescriptor? existingEmailSender = services
                .FirstOrDefault(d => d.ServiceType == typeof(IPasswordResetEmailSender));
            if (existingEmailSender is not null)
            {
                services.Remove(existingEmailSender);
            }

            services.AddSingleton<SpyPasswordResetEmailSender>();
            services.AddSingleton<IPasswordResetEmailSender>(sp =>
                sp.GetRequiredService<SpyPasswordResetEmailSender>());

            ServiceDescriptor? existingConfirmationSender = services
                .FirstOrDefault(d => d.ServiceType == typeof(IAccountConfirmationEmailSender));
            if (existingConfirmationSender is not null)
            {
                services.Remove(existingConfirmationSender);
            }

            services.AddSingleton<SpyAccountConfirmationEmailSender>();
            services.AddSingleton<IAccountConfirmationEmailSender>(sp =>
                sp.GetRequiredService<SpyAccountConfirmationEmailSender>());

            ServiceDescriptor? existingDeletionSender = services
                .FirstOrDefault(d => d.ServiceType == typeof(IAccountDeletionEmailSender));
            if (existingDeletionSender is not null)
            {
                services.Remove(existingDeletionSender);
            }

            services.AddSingleton<SpyAccountDeletionEmailSender>();
            services.AddSingleton<IAccountDeletionEmailSender>(sp =>
                sp.GetRequiredService<SpyAccountDeletionEmailSender>());

            ServiceDescriptor? existingEmailChangeSender = services
                .FirstOrDefault(d => d.ServiceType == typeof(IEmailChangeEmailSender));
            if (existingEmailChangeSender is not null)
            {
                services.Remove(existingEmailChangeSender);
            }

            services.AddSingleton<SpyEmailChangeEmailSender>();
            services.AddSingleton<IEmailChangeEmailSender>(sp =>
                sp.GetRequiredService<SpyEmailChangeEmailSender>());

            // This suite has no broker, and the consumer would keep retrying and filling the log. Its logic
            // has unit tests (EmailDispatchConsumerTests).
            RemoveHostedService<EmailDispatchConsumer>(services);

            // No job on the real clock is hosted here: a prune pass or a due deletion writes several tables
            // in one transaction, and the cleaner's TRUNCATE takes them in another order, which deadlocked
            // mid-suite (#821). Tests call the jobs directly; the unit suite tests their timing (#948). The
            // outbox relay stays: it wakes on a signal, and each of its statements touches one table.
            RemoveHostedService<OpenIddictPruneService>(services);
            RemoveHostedService<SignInSessionPruneService>(services);
            RemoveHostedService<AccountDeletionFinalizerHostedService>(services);
            services.AddSingleton<OpenIddictPruneService>();

            // The publisher is a spy, because this suite has no broker. It also plays the removed consumer,
            // so a registration still ends in a captured confirmation mail.
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

            // AddDbContext adds to the production setup, so every registered IInterceptor, the outbox wake-up
            // included, stays on the context. The failure injectors go first, so an injected commit failure
            // reaches the production interceptors as a failed commit (#781).
            services.Insert(0, ServiceDescriptor.Singleton<IInterceptor>(DbCommandFailures));
            services.Insert(1, ServiceDescriptor.Singleton<IInterceptor>(DbCommitFailures));
        });

        builder.ConfigureLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Warning);
        });
    }

    protected override IHost CreateHost(IHostBuilder builder) => HostStartOrder.BuildAndStart(builder);

    /// <summary>
    /// This suite's stand-in for the broker-to-consumer hop: the <see cref="IEmailMessageProcessor"/> chosen
    /// by message type (ADR-0038) and the same <see cref="EmailDeliveryProcessor"/> with its inbox check
    /// (ADR-0037), in a new scope per message, as <c>EmailDispatchConsumer.OnDeliveredAsync</c> does.
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

        // With error detail on, a PostgreSQL failure such as a deadlock says who held what (#821). Test data
        // needs no protection.
        _connectionString = new NpgsqlConnectionStringBuilder(_postgresContainer.GetConnectionString())
        {
            IncludeErrorDetail = true
        }.ConnectionString;

        // N-1 compat runs (ADR-0024) pre-apply the HEAD schema here; the seeder's MigrateAsync
        // then no-ops and this suite exercises its (older) code against the newer schema.
        await N1CompatSchemaSeam.ApplyIfConfiguredAsync(_postgresContainer, "auth.sql");

        // Reading Services starts the host, and the host runs the seeder at startup in every environment,
        // Testing included. The suite does not seed on its own, so a host that stopped seeding fails it.
        _ = Services;
    }

    public new virtual async Task DisposeAsync()
    {
        if (_responseTimeFloorHost is not null)
        {
            await _responseTimeFloorHost.DisposeAsync();
        }

        if (_hostWithoutOutboxRelay is not null)
        {
            await _hostWithoutOutboxRelay.DisposeAsync();
        }

        await _postgresContainer.DisposeAsync();
    }

    /// <summary>
    /// Swaps one production singleton for a test one. Strict for the same reason as
    /// <see cref="RemoveHostedService{THostedService}"/>: exactly one registration must exist.
    /// </summary>
    internal static void ReplaceSingleton<TService>(IServiceCollection services, TService replacement)
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
    /// Takes one hosted service off this host. Strict: exactly one registration must exist, so a
    /// production wiring that changes shape fails the whole suite instead of leaving a job on the clock
    /// or a removed one untested (#821).
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
