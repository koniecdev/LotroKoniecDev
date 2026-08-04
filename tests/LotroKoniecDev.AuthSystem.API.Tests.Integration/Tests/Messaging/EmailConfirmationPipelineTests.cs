using System.Collections;
using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Infrastructure.Messaging;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.AuthSystem.Persistence.Outbox;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using RabbitMQ.Client;
using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Messaging;

/// <summary>
/// The only suite where the confirmation-e-mail pipeline runs end-to-end with nothing faked
/// between the database and the SMTP seam: registration commits an outbox row, the real relay
/// publishes it through the real <see cref="RabbitMqMessagePublisher"/> to a real broker, and the
/// real <c>EmailDispatchConsumer</c> selects the registered processor, deduplicates and
/// dispatches to the spy e-mail sender. Everywhere else the broker hop is bridged in-process (<see cref="SpyMessagePublisher"/>),
/// so the consumer's ack/reject decisions never actually meet broker semantics — here they do.
/// </summary>
/// <remarks>
/// The consumer's transient-failure ladder is deliberately not driven at this level: its first
/// rung pauses 30 s before the reject, and each piece is already pinned separately — the ladder's
/// invariants in <c>EmailDispatchConsumerTests</c>, the failed-processing inbox contract in
/// <c>InboxDeduplicationTests</c>, and the delivery-limit parking in
/// <c>DeadLetterTopologyTests</c>.
/// </remarks>
public sealed class EmailConfirmationPipelineTests : IClassFixture<BrokeredAuthSystemApiFactory>, IAsyncLifetime
{
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan EmptyQueueGrace = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RecoveryTimeout = TimeSpan.FromSeconds(30);

    private readonly BrokeredAuthSystemApiFactory _factory;
    private readonly TestApiClient _apiClient;
    private readonly SpyAccountConfirmationEmailSender _confirmationEmailSpy;
    private readonly Bogus.Faker _faker = new();

    public EmailConfirmationPipelineTests(BrokeredAuthSystemApiFactory factory)
    {
        _factory = factory;
        _confirmationEmailSpy = factory.Services.GetRequiredService<SpyAccountConfirmationEmailSender>();

        JsonSerializerOptions jsonSerializerOptions =
            factory.Services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        _apiClient = new TestApiClient(factory.CreateClient(), jsonSerializerOptions);
    }

    public async Task InitializeAsync()
    {
        _confirmationEmailSpy.Reset();

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        CleanerService cleaner = scope.ServiceProvider.GetRequiredService<CleanerService>();
        await cleaner.CleanAsync();

        // The host's consumer declares the topology on startup, but the first test may reach this
        // point before that attach finished — declaring here is idempotent (proven in
        // DeadLetterTopologyTests) and guarantees the purges below have queues to purge.
        await using IConnection connection = await _factory.Broker.ConnectAsync(CancellationToken.None);
        await using IChannel channel = await connection.CreateChannelAsync();
        await RabbitMqTopologyDeclaration.DeclareAsync(channel, CancellationToken.None);
        await channel.QueuePurgeAsync(RabbitMqTopology.EmailQueue);
        await channel.QueuePurgeAsync(RabbitMqTopology.EmailDeadLetterQueue);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Pipeline_ShouldDeliverExactlyOneConfirmationEmail_WhenRegistrationCommits()
    {
        // Act — registration is the only user-facing trigger of the whole pipeline
        (RegisterRequest request, IdentityId identityId) = await UserFactory.RegisterRandomUserUnconfirmedAsync(
            _apiClient, _faker, _confirmationEmailSpy);
        await _confirmationEmailSpy.WaitForCaptureAsync(DeliveryTimeout);

        // Assert — the e-mail went out once, to the registered address, with a usable token
        _confirmationEmailSpy.LastEmail.ShouldBe(request.Email);
        _confirmationEmailSpy.LastConfirmationToken.ShouldNotBeNullOrWhiteSpace();
        _confirmationEmailSpy.CallCount.ShouldBe(1);

        // The outbox row was published and marked on the first attempt
        OutboxMessage? outboxRow = await WaitForOutboxRowAsync(
            row => row.Payload.Contains(identityId.Value.ToString(), StringComparison.OrdinalIgnoreCase)
                   && row.ProcessedOn != null);
        outboxRow.ShouldNotBeNull();
        outboxRow.Attempts.ShouldBe(0);
        outboxRow.LastError.ShouldBeNull();

        // The delivery was recorded for deduplication and nothing was left on the broker
        (await CountInboxRowsAsync(outboxRow.Id)).ShouldBe(1);
        (await GetWithinTimeoutAsync(RabbitMqTopology.EmailQueue, EmptyQueueGrace)).ShouldBeNull();
        (await GetWithinTimeoutAsync(RabbitMqTopology.EmailDeadLetterQueue, EmptyQueueGrace)).ShouldBeNull();
    }

    [Fact]
    public async Task Consumer_ShouldAckWithoutSecondEmail_WhenTheSameMessageIdIsDeliveredAgain()
    {
        // Arrange — a fully delivered registration, then its exact wire message published again
        // (the relay's crash window: published but not yet marked processed → re-published later)
        (RegisterRequest _, IdentityId identityId) = await UserFactory.RegisterRandomUserUnconfirmedAsync(
            _apiClient, _faker, _confirmationEmailSpy);
        await _confirmationEmailSpy.WaitForCaptureAsync(DeliveryTimeout);
        OutboxMessage? outboxRow = await WaitForOutboxRowAsync(
            row => row.Payload.Contains(identityId.Value.ToString(), StringComparison.OrdinalIgnoreCase)
                   && row.ProcessedOn != null);
        outboxRow.ShouldNotBeNull();
        int sendsAfterFirstDelivery = _confirmationEmailSpy.CallCount;

        // Act — same payload, same type, same message id, over the real wire
        IMessagePublisher publisher = _factory.Services.GetRequiredService<IMessagePublisher>();
        await publisher.PublishAsync(
            RabbitMqTopology.EmailConfirmationRoutingKey,
            outboxRow.Type,
            outboxRow.Payload,
            outboxRow.Id,
            CancellationToken.None);

        // Assert — the duplicate is consumed (queue drains), acked (no parking) and suppressed
        await WaitUntilQueueEmptyAsync(RabbitMqTopology.EmailQueue, DeliveryTimeout);
        await Task.Delay(TimeSpan.FromSeconds(1));
        _confirmationEmailSpy.CallCount.ShouldBe(sendsAfterFirstDelivery);
        (await CountInboxRowsAsync(outboxRow.Id)).ShouldBe(1);
        (await GetWithinTimeoutAsync(RabbitMqTopology.EmailDeadLetterQueue, EmptyQueueGrace)).ShouldBeNull();
    }

    [Theory]
    [InlineData("this is not json at all")]
    [InlineData("{}")]
    [InlineData("""{"IdentityUserId":"not-a-guid"}""")]
    public async Task Consumer_ShouldParkDeliveryInDeadLetterQueue_WhenThePayloadIsPoison(string poisonPayload)
    {
        // Act — a valid message id and a registered type, so the reject decision can only come
        // from the payload
        Guid messageId = Guid.CreateVersion7();
        IMessagePublisher publisher = _factory.Services.GetRequiredService<IMessagePublisher>();
        await publisher.PublishAsync(
            RabbitMqTopology.EmailConfirmationRoutingKey,
            nameof(EmailConfirmationRequested),
            poisonPayload,
            messageId,
            CancellationToken.None);

        // Assert — parked on first sight, no e-mail, no dedup record
        BasicGetResult dead =
            (await GetWithinTimeoutAsync(RabbitMqTopology.EmailDeadLetterQueue, DeliveryTimeout)).ShouldNotBeNull();
        Encoding.UTF8.GetString(dead.Body.ToArray()).ShouldBe(poisonPayload);
        FirstDeathReason(dead.BasicProperties).ShouldBe("rejected");
        _confirmationEmailSpy.CallCount.ShouldBe(0);
        (await CountInboxRowsAsync(messageId)).ShouldBe(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-guid")]
    public async Task Consumer_ShouldParkDeliveryInDeadLetterQueue_WhenTheMessageIdIsUnusable(string? rawMessageId)
    {
        // Arrange — a real unconfirmed user, so a processed delivery WOULD send an e-mail: the
        // only thing broken about this message is the id the inbox would deduplicate on
        (RegisterRequest _, IdentityId identityId) = await UserFactory.RegisterRandomUserUnconfirmedAsync(
            _apiClient, _faker, _confirmationEmailSpy);
        await _confirmationEmailSpy.WaitForCaptureAsync(DeliveryTimeout);
        _confirmationEmailSpy.Reset();
        string validPayload = JsonSerializer.Serialize(new EmailConfirmationRequested(identityId.Value));

        // Act — raw publish: the real publisher always stamps a valid id, the wire does not
        await PublishRawAsync(validPayload, rawMessageId);

        // Assert — parked instead of processed blind, and no e-mail went out
        BasicGetResult dead =
            (await GetWithinTimeoutAsync(RabbitMqTopology.EmailDeadLetterQueue, DeliveryTimeout)).ShouldNotBeNull();
        Encoding.UTF8.GetString(dead.Body.ToArray()).ShouldBe(validPayload);
        FirstDeathReason(dead.BasicProperties).ShouldBe("rejected");
        _confirmationEmailSpy.CallCount.ShouldBe(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("TypeNobodyRegistered")]
    public async Task Consumer_ShouldParkDeliveryInDeadLetterQueue_WhenTheMessageTypeIsMissingOrUnregistered(
        string? messageType)
    {
        // Arrange — a real unconfirmed user and a perfectly readable payload: the only thing
        // broken about this delivery is the type property the registry selects processors by
        // (ADR-0038), so a redelivery could never fix it
        (RegisterRequest _, IdentityId identityId) = await UserFactory.RegisterRandomUserUnconfirmedAsync(
            _apiClient, _faker, _confirmationEmailSpy);
        await _confirmationEmailSpy.WaitForCaptureAsync(DeliveryTimeout);
        _confirmationEmailSpy.Reset();
        Guid messageId = Guid.CreateVersion7();
        string validPayload = JsonSerializer.Serialize(new EmailConfirmationRequested(identityId.Value));

        // Act — raw publish: the real publisher refuses to send without a type, the wire does not
        await PublishRawAsync(validPayload, messageId.ToString(), messageType);

        // Assert — parked on first sight, no e-mail, no dedup record
        BasicGetResult dead =
            (await GetWithinTimeoutAsync(RabbitMqTopology.EmailDeadLetterQueue, DeliveryTimeout)).ShouldNotBeNull();
        Encoding.UTF8.GetString(dead.Body.ToArray()).ShouldBe(validPayload);
        FirstDeathReason(dead.BasicProperties).ShouldBe("rejected");
        _confirmationEmailSpy.CallCount.ShouldBe(0);
        (await CountInboxRowsAsync(messageId)).ShouldBe(0);
    }

    [Fact]
    public async Task Consumer_ShouldAckWithoutEmailAndWithoutParking_WhenTheUserNoLongerExists()
    {
        // Act — a payload whose user id matches no account (registered, then vanished): redelivery
        // could never change the outcome, so the ack-and-record contract applies, not the DLQ
        Guid messageId = Guid.CreateVersion7();
        string payload = JsonSerializer.Serialize(new EmailConfirmationRequested(Guid.CreateVersion7()));
        IMessagePublisher publisher = _factory.Services.GetRequiredService<IMessagePublisher>();
        await publisher.PublishAsync(
            RabbitMqTopology.EmailConfirmationRoutingKey,
            nameof(EmailConfirmationRequested),
            payload,
            messageId,
            CancellationToken.None);

        // Assert — the inbox record doubles as the "consumer finished" signal
        int inboxRows = await WaitForInboxRowsAsync(messageId, DeliveryTimeout);
        inboxRows.ShouldBe(1);
        _confirmationEmailSpy.CallCount.ShouldBe(0);
        (await GetWithinTimeoutAsync(RabbitMqTopology.EmailQueue, EmptyQueueGrace)).ShouldBeNull();
        (await GetWithinTimeoutAsync(RabbitMqTopology.EmailDeadLetterQueue, EmptyQueueGrace)).ShouldBeNull();
    }

    [Fact]
    public async Task Pipeline_ShouldRecoverAndDeliver_WhenTheBrokerDropsEveryConnection()
    {
        // Arrange — a warm pipeline (consumer attached, publisher connected), then the broker
        // kills every client connection: the deploy/restart scenario both sides must survive
        await UserFactory.RegisterRandomUserUnconfirmedAsync(_apiClient, _faker, _confirmationEmailSpy);
        await _confirmationEmailSpy.WaitForCaptureAsync(DeliveryTimeout);
        await _factory.Broker.CloseAllConnectionsAsync();

        // Act — a registration right after the cut; its own nudge may race the dead connection
        (RegisterRequest secondRequest, IdentityId secondIdentityId) =
            await UserFactory.RegisterRandomUserUnconfirmedAsync(_apiClient, _faker, _confirmationEmailSpy);

        // A spent nudge would leave the relay in its retry backoff (30 s first rung); production
        // gets re-nudged by every later commit, so the wait re-nudges instead of paying that rung
        // — what this test pins is recovery, the retry cadence is OutboxRelayTests' subject.
        OutboxSignal outboxSignal = _factory.Services.GetRequiredService<OutboxSignal>();
        using CancellationTokenSource recoveryWindow = new(RecoveryTimeout);
        while (_confirmationEmailSpy.LastEmail is null && !recoveryWindow.IsCancellationRequested)
        {
            outboxSignal.Notify();
            await Task.Delay(500);
        }

        // Assert — the second e-mail arrived through rebuilt connections, exactly once
        _confirmationEmailSpy.LastEmail.ShouldBe(secondRequest.Email);
        _confirmationEmailSpy.CallCount.ShouldBe(1);

        OutboxMessage? recoveredRow = await WaitForOutboxRowAsync(
            row => row.Payload.Contains(secondIdentityId.Value.ToString(), StringComparison.OrdinalIgnoreCase)
                   && row.ProcessedOn != null);
        recoveredRow.ShouldNotBeNull();
        (await GetWithinTimeoutAsync(RabbitMqTopology.EmailQueue, EmptyQueueGrace)).ShouldBeNull();
        (await GetWithinTimeoutAsync(RabbitMqTopology.EmailDeadLetterQueue, EmptyQueueGrace)).ShouldBeNull();
    }

    [Fact]
    public async Task GetHealth_ShouldReportTheBrokerHealthy_WhenTheBrokerIsUp()
    {
        // Act — the base factory proves the Unhealthy side against a dead port; this host is the
        // only one where RabbitMqHealthCheck can prove its Healthy verdict against a live broker
        HttpResponseMessage response = await _apiClient.Http.GetAsync(new Uri("/health", UriKind.Relative));
        string body = await response.Content.ReadAsStringAsync();

        // Assert — overall status still reflects the deliberately dead SMTP port, so only the
        // rabbitmq entry is this test's subject
        using JsonDocument report = JsonDocument.Parse(body);
        JsonElement rabbitMqCheck = report.RootElement.GetProperty("checks").EnumerateArray()
            .Single(check => check.GetProperty("name").GetString() == "rabbitmq");
        rabbitMqCheck.GetProperty("status").GetString().ShouldBe("Healthy");
    }

    private async Task PublishRawAsync(string payload, string? messageId, string? messageType = null)
    {
        await using IConnection connection = await _factory.Broker.ConnectAsync(CancellationToken.None);
        await using IChannel channel = await connection.CreateChannelAsync();

        BasicProperties properties = new()
        {
            MessageId = messageId,
            Type = messageType,
            DeliveryMode = DeliveryModes.Persistent
        };

        await channel.BasicPublishAsync(
            exchange: RabbitMqTopology.EmailsExchange,
            routingKey: RabbitMqTopology.EmailConfirmationRoutingKey,
            mandatory: true,
            basicProperties: properties,
            body: Encoding.UTF8.GetBytes(payload));
    }

    /// <summary>
    /// Polls the queue until a delivery arrives or the timeout passes — routing and dead-lettering
    /// are asynchronous inside the broker. The read is auto-acked: every caller either asserts on
    /// the delivery (test over either way) or expects null.
    /// </summary>
    private async Task<BasicGetResult?> GetWithinTimeoutAsync(string queue, TimeSpan timeout)
    {
        await using IConnection connection = await _factory.Broker.ConnectAsync(CancellationToken.None);
        await using IChannel channel = await connection.CreateChannelAsync();

        Stopwatch elapsed = Stopwatch.StartNew();

        while (true)
        {
            BasicGetResult? delivery = await channel.BasicGetAsync(queue, autoAck: true);
            if (delivery is not null)
            {
                return delivery;
            }

            if (elapsed.Elapsed >= timeout)
            {
                return null;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
    }

    private async Task WaitUntilQueueEmptyAsync(string queue, TimeSpan timeout)
    {
        await using IConnection connection = await _factory.Broker.ConnectAsync(CancellationToken.None);
        await using IChannel channel = await connection.CreateChannelAsync();

        Stopwatch elapsed = Stopwatch.StartNew();

        while (elapsed.Elapsed < timeout)
        {
            uint messageCount = await channel.MessageCountAsync(queue);
            if (messageCount == 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
    }

    /// <summary>
    /// Polls the outbox until a row matches or the timeout passes, then returns the latest
    /// snapshot (or null) — the assertions on it stay in the test body.
    /// </summary>
    private async Task<OutboxMessage?> WaitForOutboxRowAsync(Func<OutboxMessage, bool> predicate)
    {
        using CancellationTokenSource timeout = new(DeliveryTimeout);

        while (true)
        {
            await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

            List<OutboxMessage> rows = await db.OutboxMessages.AsNoTracking().ToListAsync();
            OutboxMessage? match = rows.FirstOrDefault(predicate);

            if (match is not null || timeout.IsCancellationRequested)
            {
                return match;
            }

            await Task.Delay(100);
        }
    }

    private async Task<int> WaitForInboxRowsAsync(Guid messageId, TimeSpan timeout)
    {
        using CancellationTokenSource waitWindow = new(timeout);

        while (true)
        {
            int count = await CountInboxRowsAsync(messageId);
            if (count > 0 || waitWindow.IsCancellationRequested)
            {
                return count;
            }

            await Task.Delay(100);
        }
    }

    private async Task<int> CountInboxRowsAsync(Guid messageId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.InboxMessages.AsNoTracking().CountAsync(row => row.MessageId == messageId);
    }

    /// <summary>
    /// Reads the reason of the first (most recent) <c>x-death</c> entry the broker stamped on a
    /// dead-lettered message.
    /// </summary>
    private static string? FirstDeathReason(IReadOnlyBasicProperties properties)
    {
        if (properties.Headers is not { } headers
            || !headers.TryGetValue("x-death", out object? deathsRaw)
            || deathsRaw is not IList { Count: > 0 } deaths
            || deaths[0] is not IDictionary firstDeath
            || !firstDeath.Contains("reason"))
        {
            return null;
        }

        return firstDeath["reason"] is byte[] reason ? Encoding.UTF8.GetString(reason) : null;
    }
}
