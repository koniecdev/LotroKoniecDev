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
/// erasures, and the consumer pauses 50 ms instead of up to 15 minutes and checks its subscription
/// every 200 ms instead of every 30 seconds.
/// </summary>
#pragma warning disable CA1515
public sealed class BrokeredTranslationSystemApiFactory : TranslationSystemApiFactory
#pragma warning restore CA1515
{
    private static readonly TimeSpan[] ShortRetryBackoffs = [TimeSpan.FromMilliseconds(50)];

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
            services.AddHostedService(serviceProvider => new AccountErasedConsumer(
                serviceProvider.GetRequiredService<IOptions<RabbitMqSettings>>(),
                serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                serviceProvider.GetRequiredService<ILogger<AccountErasedConsumer>>(),
                ShortRetryBackoffs,
                TimeSpan.FromMilliseconds(200)));

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
