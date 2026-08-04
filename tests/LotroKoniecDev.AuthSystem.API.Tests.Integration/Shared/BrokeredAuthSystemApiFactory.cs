using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.API.BackgroundServices;
using LotroKoniecDev.AuthSystem.Infrastructure.Messaging;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;

/// <summary>
/// The brokered twin of <see cref="AuthSystemApiFactory"/>: the base host swaps the publisher for
/// a spy and removes the consumer (no broker in that suite), and this factory undoes exactly that
/// against a real broker container — the real <see cref="RabbitMqMessagePublisher"/> returns, the
/// real <see cref="EmailDispatchConsumer"/> is put back, and the RabbitMq configuration points
/// at the container. The SMTP seam stays spied, so tests still observe delivered e-mails without
/// sending any. This is the only host where the full outbox → relay → broker → consumer pipeline
/// exists in one process.
/// </summary>
public sealed class BrokeredAuthSystemApiFactory : AuthSystemApiFactory
{
    public RabbitMqBrokerFixture Broker { get; } = new();

    /// <summary>
    /// The broker container's coordinates, available once <see cref="InitializeAsync"/> started it
    /// — tests use them to open raw assertion channels next to the host's own clients.
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
            services.AddHostedService<EmailDispatchConsumer>();
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
