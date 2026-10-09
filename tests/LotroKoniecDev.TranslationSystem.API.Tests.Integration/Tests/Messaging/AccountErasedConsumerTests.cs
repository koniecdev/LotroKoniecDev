using System.Text;
using System.Text.Json;
using LotroKoniecDev.SharedKernel.IntegrationEvents;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.API.Messaging;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Messaging;

/// <summary>
/// The TMS end of ADR-0065 against a real broker: an <see cref="AccountErased"/> published the way the
/// AuthSystem's relay publishes it erases the profile, and a message the consumer can never handle is
/// parked in the dead-letter queue instead of looping or vanishing.
/// </summary>
public sealed class AccountErasedConsumerTests : IClassFixture<BrokeredTranslationSystemApiFactory>, IAsyncLifetime
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);

    private readonly BrokeredTranslationSystemApiFactory _factory;
    private IConnection? _connection;
    private IChannel? _channel;

    public AccountErasedConsumerTests(BrokeredTranslationSystemApiFactory factory)
    {
        _factory = factory;
    }

    private IChannel Channel => _channel ?? throw new InvalidOperationException("The channel is not open.");

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync("TRUNCATE translation.\"Translators\" CASCADE;");

        _connection = await _factory.ConnectToBrokerAsync(CancellationToken.None);
        _channel = await _connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true));

        // The AuthSystem declares the exchange; the TMS only checks it exists. The queue is then bound
        // before the first publish, even if the consumer has not attached yet.
        await Channel.ExchangeDeclareAsync(AccountEvents.Exchange, ExchangeType.Topic, durable: true, autoDelete: false);
        await AccountEventsTopology.DeclareAsync(Channel, CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.DisposeAsync();
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task AccountErased_PublishedLikeTheRelayDoes_ShouldEraseTheProfile()
    {
        // Arrange
        Guid identity = Guid.NewGuid();
        TranslatorId translatorId = await SeedTranslatorAsync(identity);

        // Act
        await PublishAsync(nameof(AccountErased), JsonSerializer.Serialize(new AccountErased(identity)), Guid.NewGuid());

        // Assert
        Translator profile = await WaitForProfileAsync(
            translatorId, translator => translator.DisplayName.Value == Translator.ErasedDisplayName);
        profile.DisplayName.Value.ShouldBe("Usunięte konto");
        profile.Email.ShouldBeNull();
    }

    [Fact]
    public async Task Message_OfAnUnknownType_ShouldBeParkedInTheDeadLetterQueue()
    {
        // Arrange
        Guid messageId = Guid.NewGuid();

        // Act
        await PublishAsync("EmailChangeRequested", JsonSerializer.Serialize(new AccountErased(Guid.NewGuid())), messageId);

        // Assert
        (await WaitForDeadLetterAsync(messageId)).ShouldBeTrue();
    }

    [Fact]
    public async Task AccountErased_WithAnUnreadablePayload_ShouldBeParkedInTheDeadLetterQueue()
    {
        // Arrange
        Guid messageId = Guid.NewGuid();

        // Act
        await PublishAsync(nameof(AccountErased), "{\"IdentityUserId\":\"not-a-guid\"}", messageId);

        // Assert
        (await WaitForDeadLetterAsync(messageId)).ShouldBeTrue();
    }

    private async Task<TranslatorId> SeedTranslatorAsync(Guid identity)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();

        Translator translator = Translator.Create(
            IdentityId.FromValue(identity),
            DisplayName.Create("Frodo Baggins").Value,
            Email.Create("frodo@shire.me").Value,
            Now).Value;
        dbContext.Translators.Add(translator);
        await dbContext.SaveChangesAsync();

        return translator.Id;
    }

    private async Task PublishAsync(string type, string payload, Guid messageId)
    {
        BasicProperties properties = new()
        {
            MessageId = messageId.ToString(),
            Type = type,
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent
        };

        await Channel.BasicPublishAsync(
            exchange: AccountEvents.Exchange,
            routingKey: AccountEvents.ErasedRoutingKey,
            mandatory: true,
            basicProperties: properties,
            body: Encoding.UTF8.GetBytes(payload));
    }

    private async Task<Translator> WaitForProfileAsync(TranslatorId id, Func<Translator, bool> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + WaitLimit;

        while (true)
        {
            using (IServiceScope scope = _factory.Services.CreateScope())
            {
                ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
                Translator translator = await dbContext.Translators.AsNoTracking().SingleAsync(row => row.Id == id);

                if (condition(translator) || DateTimeOffset.UtcNow > deadline)
                {
                    return translator;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
    }

    private async Task<bool> WaitForDeadLetterAsync(Guid messageId)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + WaitLimit;

        while (DateTimeOffset.UtcNow <= deadline)
        {
            BasicGetResult? deadLetter = await Channel.BasicGetAsync(AccountEventsTopology.ErasedDeadLetterQueue, autoAck: true);
            if (deadLetter?.BasicProperties.MessageId == messageId.ToString())
            {
                return true;
            }

            if (deadLetter is null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }
        }

        return false;
    }
}
