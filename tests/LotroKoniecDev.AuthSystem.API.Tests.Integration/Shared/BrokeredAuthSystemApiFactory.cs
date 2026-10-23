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
/// The twin of <see cref="AuthSystemApiFactory"/> with a real broker: the real
/// <see cref="RabbitMqMessagePublisher"/> and <see cref="EmailDispatchConsumer"/> come back, and SMTP stays a
/// spy. It is the only host where the whole pipeline runs in one process. The consumer's timings are shorter
/// but keep production's order: a look every second, a reconnect after 200 ms and recovery every 100 ms, so
/// recovery still comes before the second look.
/// </summary>
public sealed class BrokeredAuthSystemApiFactory : AuthSystemApiFactory
{
    private static readonly TimeSpan[] ShortConnectBackoffs = [TimeSpan.FromMilliseconds(200)];
    private static readonly TimeSpan ShortWatchInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ShortRecoveryInterval = TimeSpan.FromMilliseconds(100);

    public RabbitMqBrokerFixture Broker { get; } = new();

    /// <summary>
    /// Available once <see cref="InitializeAsync"/> started the broker. Tests open their own channels with it.
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
                    serviceProvider.GetRequiredService<TimeProvider>(),
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
