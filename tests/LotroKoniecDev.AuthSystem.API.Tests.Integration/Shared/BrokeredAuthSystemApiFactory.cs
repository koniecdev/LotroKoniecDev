using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.API.BackgroundServices;
using LotroKoniecDev.AuthSystem.Infrastructure.Messaging;
using RabbitMQ.Client;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;

/// <summary>
/// The twin of <see cref="AuthSystemApiFactory"/> that uses a real broker. The base host replaces the
/// publisher with a spy and removes the consumer, because that suite has no broker, and this factory
/// undoes exactly that against a real broker container: the real
/// <see cref="RabbitMqMessagePublisher"/> comes back, the real <see cref="EmailDispatchConsumer"/> is
/// added again, and the RabbitMq settings point at the container.
/// SMTP is still a spy, so tests can see the e-mails without sending any. This is the only host where
/// the whole pipeline, outbox to relay to broker to consumer, exists in one process.
/// The consumer runs faster than in production but in the same order. It checks its subscription every
/// second instead of every 30 seconds, and waits 200 ms instead of up to a minute before it connects
/// again, so a test can watch it attach again in seconds. Its automatic recovery retries every 100 ms
/// instead of every 5 seconds, so after a dropped connection recovery still comes first, before the
/// second look, as it does in production.
/// </summary>
public sealed class BrokeredAuthSystemApiFactory : AuthSystemApiFactory
{
    private static readonly TimeSpan[] ShortConnectBackoffs = [TimeSpan.FromMilliseconds(200)];
    private static readonly TimeSpan ShortWatchInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ShortRecoveryInterval = TimeSpan.FromMilliseconds(100);

    public RabbitMqBrokerFixture Broker { get; } = new();

    /// <summary>
    /// The broker container's address and credentials, available once <see cref="InitializeAsync"/>
    /// has started it. Tests use them to open their own channels next to the host's clients.
    /// </summary>
    public RabbitMqOptions BrokerOptions
    {
        get
        {
            return field ?? throw new InvalidOperationException("The broker container has not been started yet.");
        }
        private set;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            // Registered after the base's dead-port collection, so these keys win the merge.
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "RabbitMq:Host", BrokerOptions.Host },
                { "RabbitMq:Port", BrokerOptions.Port.ToString(CultureInfo.InvariantCulture) },
                { "RabbitMq:Username", BrokerOptions.Username },
                { "RabbitMq:Password", BrokerOptions.Password },
                { "RabbitMq:VirtualHost", BrokerOptions.VirtualHost }
            });
        });

        builder.ConfigureTestServices(services =>
        {
            ServiceDescriptor? spyPublisher = services
                .FirstOrDefault(d => d.ServiceType == typeof(IMessagePublisher));
            if (spyPublisher is not null)
            {
                services.Remove(spyPublisher);
            }

            services.AddSingleton<IMessagePublisher, RabbitMqMessagePublisher>();
            services.AddHostedService(serviceProvider =>
            {
                ConnectionFactory connectionFactory = RabbitMqConnectionFactoryBuilder.Build(
                    serviceProvider.GetRequiredService<IOptions<RabbitMqOptions>>().Value,
                    EmailDispatchConsumer.ClientProvidedName);
                connectionFactory.NetworkRecoveryInterval = ShortRecoveryInterval;

                return new EmailDispatchConsumer(
                    connectionFactory,
                    serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                    serviceProvider.GetRequiredService<ILogger<EmailDispatchConsumer>>(),
                    ShortConnectBackoffs,
                    ShortWatchInterval);
            });
        });
    }

    public override async Task InitializeAsync()
    {
        // The broker must be up before the base touches Services: building the host runs the
        // configuration callback above, which needs the container's mapped port.
        await Broker.InitializeAsync();
        BrokerOptions = Broker.BuildOptions();
        await base.InitializeAsync();
    }

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await Broker.DisposeAsync();
    }
}
