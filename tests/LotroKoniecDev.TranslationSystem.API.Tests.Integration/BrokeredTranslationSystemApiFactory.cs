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
/// <see cref="TranslationSystemApiFactory"/> with a real RabbitMQ container and the real
/// <see cref="AccountErasedConsumer"/>: the only host where broker, consumer and database run together
/// (ADR-0065). <see cref="ErasureFailures"/> fails the next erasures, and every pause is shortened (50 ms
/// retry, 200 ms reconnect and watch, 100 ms recovery) so the slow paths run in seconds. Recovery still
/// starts before the second look, as in production, but the margin is small; no test here drops a
/// connection.
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
                    serviceProvider.GetRequiredService<TimeProvider>(),
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
        // Before the base touches Services: building the host needs the container's mapped port.
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
