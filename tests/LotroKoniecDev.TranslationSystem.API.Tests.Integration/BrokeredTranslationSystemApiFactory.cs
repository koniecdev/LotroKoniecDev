using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.Tests.Shared;
using LotroKoniecDev.TranslationSystem.API.Features.Translators;
using LotroKoniecDev.TranslationSystem.API.Messaging;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration;

/// <summary>
/// The twin of <see cref="TranslationSystemApiFactory"/> with a real broker. The base host has no broker
/// and removes the account event consumer, and this factory undoes exactly that: the settings point at
/// a RabbitMQ container and the real <see cref="AccountErasedConsumer"/> runs again. It is the only
/// host where the TMS half of an erasure, broker to consumer to database, runs in one process
/// (ADR-0065).
/// Two seams make the slow paths testable in seconds: <see cref="ErasureFailures"/> fails the next
/// erasures, and the consumer runs faster than in production. It pauses 50 ms instead of up to 15
/// minutes before a failed message goes back, waits 200 ms instead of up to a minute before it connects
/// again, and checks its subscription every 200 ms instead of every 30 seconds. Its automatic recovery
/// retries every 100 ms instead of every 5 seconds, so after a dropped connection recovery starts before
/// the second look, as in production. The margin is small, so on a slow run the consumer may attach
/// again instead. No test here drops a connection.
/// </summary>
#pragma warning disable CA1515
public sealed class BrokeredTranslationSystemApiFactory : TranslationSystemApiFactory
#pragma warning restore CA1515
{
    private static readonly TimeSpan[] ShortConnectBackoffs = [TimeSpan.FromMilliseconds(200)];
    private static readonly TimeSpan[] ShortRetryBackoffs = [TimeSpan.FromMilliseconds(50)];
    private static readonly TimeSpan ShortWatchInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ShortRecoveryInterval = TimeSpan.FromMilliseconds(100);

    private readonly RabbitMqContainer _broker = new RabbitMqBuilder(RabbitMqImage.Name).Build();

    public ErasureFailures ErasureFailures { get; } = new();

    private Uri BrokerUri
    {
        get
        {
            return field ?? throw new InvalidOperationException("The broker container has not been started yet.");
        }
        set;
    }

    public async Task<IConnection> ConnectToBrokerAsync(CancellationToken cancellationToken)
    {
        ConnectionFactory connectionFactory = new() { Uri = BrokerUri };
        return await connectionFactory.CreateConnectionAsync(cancellationToken);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            string[] userInfo = BrokerUri.UserInfo.Split(':');

            // Added after the base's dead-port values, so these keys win the merge.
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "RabbitMq:Host", BrokerUri.Host },
                { "RabbitMq:Port", BrokerUri.Port.ToString(CultureInfo.InvariantCulture) },
                { "RabbitMq:Username", Uri.UnescapeDataString(userInfo[0]) },
                { "RabbitMq:Password", Uri.UnescapeDataString(userInfo[1]) },
                { "RabbitMq:VirtualHost", "/" }
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.AddHostedService(serviceProvider =>
            {
                ConnectionFactory connectionFactory = AccountErasedConsumer.BuildConnectionFactory(
                    serviceProvider.GetRequiredService<IOptions<RabbitMqSettings>>().Value);
                connectionFactory.NetworkRecoveryInterval = ShortRecoveryInterval;

                return new AccountErasedConsumer(
                    connectionFactory,
                    serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                    serviceProvider.GetRequiredService<ILogger<AccountErasedConsumer>>(),
                    ShortConnectBackoffs,
                    ShortRetryBackoffs,
                    ShortWatchInterval);
            });

            ServiceDescriptor handler = services.Single(descriptor =>
                descriptor.ServiceType == typeof(ICommandHandler<EraseTranslatorProfile.Command, Result>));
            services.Remove(handler);
            services.AddScoped<ICommandHandler<EraseTranslatorProfile.Command, Result>>(serviceProvider =>
                new FailingEraseHandler(
                    ActivatorUtilities.CreateInstance<EraseTranslatorProfile.Handler>(serviceProvider),
                    ErasureFailures));
        });
    }

    public override async Task InitializeAsync()
    {
        // The broker must be up before the base touches Services: building the host reads the
        // configuration above, which needs the container's mapped port.
        await _broker.StartAsync();
        BrokerUri = new Uri(_broker.GetConnectionString());
        await base.InitializeAsync();
    }

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await _broker.DisposeAsync();
    }
}
