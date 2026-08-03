using System.Text.Json;
using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Services.Emails;
using LotroKoniecDev.AuthSystem.Infrastructure.Messaging;
using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Monads;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace LotroKoniecDev.AuthSystem.API.BackgroundServices;

/// <summary>
/// Consumes <see cref="RabbitMqTopology.EmailQueue"/> and turns each delivery into a confirmation
/// e-mail via <see cref="EmailConfirmationRequestProcessor"/>. Push-based, not polled: after
/// <c>BasicConsumeAsync</c> registers the subscription, the broker pushes deliveries over the open
/// connection and the client library invokes <see cref="OnDeliveredAsync"/> per message —
/// <see cref="ExecuteAsync"/> only sets this up and then parks until shutdown.
/// </summary>
/// <remarks>
/// Acknowledgement is manual (<c>autoAck: false</c>) and happens only after the processor
/// finished: a crash mid-send leaves the delivery unacked, the broker returns it to the queue,
/// and the next start redelivers — at-least-once, matching the outbox's own semantics
/// (the processor stays idempotent, see its remarks). Failures split three ways (ADR-0036):
/// poison payloads are rejected into the dead-letter queue immediately, transient failures are
/// requeued behind an escalating pause, and the broker itself parks a message that exhausts
/// <see cref="RabbitMqTopology.EmailDeliveryLimit"/> — so no failure loops forever and none is
/// silently lost. Failed deliveries use <c>basic.reject</c>, never <c>basic.nack</c>: since
/// RabbitMQ 4.3 only rejects (and connection losses) increment the <c>x-delivery-count</c> the
/// delivery limit is measured against — a nack-requeue is an "explicit return" the broker
/// redelivers forever without counting. The broker being down must never block application
/// startup, so connecting
/// happens here with escalating backoff, and the client's automatic recovery re-attaches the
/// consumer if the connection drops later.
/// </remarks>
internal sealed partial class EmailConfirmationConsumer : BackgroundService
{
    /// <summary>
    /// Escalating wait between initial connection attempts. The ceiling stays low (the broker is
    /// a container on the same box, outages are deploy-length), and unlike the outbox relay this
    /// retry costs no database compute — it only re-tries a local TCP connect.
    /// </summary>
    private static readonly TimeSpan[] ConnectBackoffs =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromMinutes(1)
    ];

    /// <summary>
    /// Pause before a transient failure is rejected back to the queue, indexed by the broker's
    /// redelivery count — one entry per redelivery that
    /// <see cref="RabbitMqTopology.EmailDeliveryLimit"/> allows, so the two move together. With a
    /// prefetch of one the broker redelivers immediately after the reject; without a pause a down
    /// SMTP relay would spin the redeliver-fail loop hot and burn through the delivery limit in
    /// seconds. Escalating instead of flat, because the ladder must in total outlast a realistic
    /// SMTP outage (~30 min) before the message parks in the DLQ. Pausing in-process blocks this
    /// consumer, which is harmless — every message in the queue needs the same SMTP relay, so
    /// none of the waiting ones could succeed either. Hard ceiling: the pause holds the delivery
    /// unacked, and the broker kills the channel when an ack takes longer than its consumer
    /// timeout (30 min default) — every entry must stay well under that.
    /// </summary>
    private static readonly TimeSpan[] RedeliveryBackoffs =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(15)
    ];

    private readonly RabbitMqOptions _settings;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EmailConfirmationConsumer> _logger;

    private IConnection? _connection;
    private IChannel? _channel;

    public EmailConfirmationConsumer(
        IOptions<RabbitMqOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<EmailConfirmationConsumer> logger)
    {
        _settings = options.Value;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            IChannel channel = await ConnectWithRetryAsync(stoppingToken);

            AsyncEventingBasicConsumer consumer = new(channel);
            consumer.ReceivedAsync += (_, delivery) => OnDeliveredAsync(channel, delivery, stoppingToken);

            await channel.BasicConsumeAsync(
                queue: RabbitMqTopology.EmailQueue,
                autoAck: false,
                consumer: consumer,
                cancellationToken: stoppingToken);

            LogStarted(_logger, RabbitMqTopology.EmailQueue);

            // All work happens in OnDeliveredAsync on the library's dispatch loop; this task only
            // keeps the service (and with it the channel) alive until shutdown.
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown.
        }
        finally
        {
            await CloseAsync();
        }
    }

    private async Task<IChannel> ConnectWithRetryAsync(CancellationToken stoppingToken)
    {
        int failedAttempts = 0;

        while (true)
        {
            try
            {
                ConnectionFactory connectionFactory =
                    RabbitMqConnectionFactoryBuilder.Build(_settings, "lotro-auth-api-email-consumer");

                _connection = await connectionFactory.CreateConnectionAsync(stoppingToken);
                IChannel channel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);
                await RabbitMqTopologyDeclaration.DeclareAsync(channel, stoppingToken);

                // Prefetch 1: the broker hands over the next message only after the previous one
                // was acked, so a backlog never floods this process and e-mails go out one by one.
                await channel.BasicQosAsync(
                    prefetchSize: 0,
                    prefetchCount: 1,
                    global: false,
                    cancellationToken: stoppingToken);

                _channel = channel;
                return channel;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failedAttempts++;
                TimeSpan wait = ConnectBackoffs[Math.Min(failedAttempts - 1, ConnectBackoffs.Length - 1)];
                LogConnectFailed(_logger, ex, wait.TotalSeconds);
                await Task.Delay(wait, stoppingToken);
            }
        }
    }

    /// <summary>
    /// Handles one delivery end-to-end. Every path must end in exactly one ack or reject — an
    /// exception escaping this handler would be swallowed by the client library and leave the
    /// delivery unacked (stuck) until the channel dies.
    /// </summary>
    private async Task OnDeliveredAsync(
        IChannel channel,
        BasicDeliverEventArgs delivery,
        CancellationToken stoppingToken)
    {
        try
        {
            EmailConfirmationRequested? message = TryDeserialize(delivery.Body.Span);
            if (message is null || message.IdentityUserId == Guid.Empty)
            {
                // Poison: no amount of redelivery fixes an unreadable payload, so it is rejected
                // on first sight — the broker dead-letters it into the parking lot for a human.
                LogPoisonMessage(_logger, delivery.BasicProperties.MessageId);
                await channel.BasicRejectAsync(
                    delivery.DeliveryTag,
                    requeue: false,
                    cancellationToken: stoppingToken);
                return;
            }

            Result ackDecision;
            await using (AsyncServiceScope scope = _scopeFactory.CreateAsyncScope())
            {
                EmailConfirmationRequestProcessor processor =
                    scope.ServiceProvider.GetRequiredService<EmailConfirmationRequestProcessor>();
                ackDecision = await processor.ProcessAsync(message, stoppingToken);
            }

            if (ackDecision.IsSuccess)
            {
                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
                return;
            }

            int redeliveries = RedeliveryCount.Read(delivery.BasicProperties.Headers);
            if (redeliveries >= RabbitMqTopology.EmailDeliveryLimit)
            {
                // Final attempt: this reject pushes the count past the delivery limit, so the
                // broker parks the message in the DLQ instead of redelivering — no pause needed.
                LogRetriesExhausted(
                    _logger,
                    delivery.BasicProperties.MessageId,
                    ackDecision.Error,
                    RabbitMqTopology.EmailDeliveryLimit);
                await channel.BasicRejectAsync(
                    delivery.DeliveryTag,
                    requeue: true,
                    cancellationToken: stoppingToken);
                return;
            }

            LogTransientFailure(
                _logger,
                delivery.BasicProperties.MessageId,
                ackDecision.Error,
                redeliveries + 1,
                RabbitMqTopology.EmailDeliveryLimit);
            await Task.Delay(RedeliveryBackoffs[Math.Min(redeliveries, RedeliveryBackoffs.Length - 1)], stoppingToken);
            await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: true, cancellationToken: stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown mid-message: deliberately neither ack nor reject — closing the channel
            // returns the unacked delivery to the queue and the next start picks it up.
        }
        catch (Exception ex)
        {
            LogUnexpectedError(_logger, ex, delivery.BasicProperties.MessageId);
            await TryRequeueAsync(channel, delivery.DeliveryTag, stoppingToken);
        }
    }

    /// <summary>
    /// Best-effort reject for the unexpected-exception path: when even the reject fails (typically
    /// a dead channel), the delivery is unacked anyway and the broker requeues it on channel close
    /// — and that connection loss increments the delivery count too, so even a crash loop is
    /// bounded by the delivery limit.
    /// </summary>
    private async Task TryRequeueAsync(IChannel channel, ulong deliveryTag, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(RedeliveryBackoffs[0], stoppingToken);
            await channel.BasicRejectAsync(deliveryTag, requeue: true, cancellationToken: stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Shutdown while backing off; the unacked delivery requeues on channel close.
        }
        catch (Exception ex)
        {
            LogUnexpectedError(_logger, ex, messageId: null);
        }
    }

    private static EmailConfirmationRequested? TryDeserialize(ReadOnlySpan<byte> body)
    {
        try
        {
            return JsonSerializer.Deserialize<EmailConfirmationRequested>(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task CloseAsync()
    {
        if (_channel is not null)
        {
            try
            {
                await _channel.DisposeAsync();
            }
            catch (Exception ex)
            {
                LogTeardownWarning(_logger, ex);
            }
        }

        if (_connection is not null)
        {
            try
            {
                await _connection.DisposeAsync();
            }
            catch (Exception ex)
            {
                LogTeardownWarning(_logger, ex);
            }
        }
    }

    [LoggerMessage(
        EventId = EventIds.EmailConsumerStarted,
        Level = LogLevel.Information,
        Message = "Consuming e-mail messages from queue {Queue}")]
    private static partial void LogStarted(ILogger logger, string queue);

    [LoggerMessage(
        EventId = EventIds.EmailConsumerConnectFailed,
        Level = LogLevel.Warning,
        Message = "Connecting the e-mail consumer to the broker failed; retrying in {DelaySeconds}s")]
    private static partial void LogConnectFailed(ILogger logger, Exception exception, double delaySeconds);

    [LoggerMessage(
        EventId = EventIds.EmailConsumerPoisonMessage,
        Level = LogLevel.Error,
        Message = "Rejecting poison message {MessageId} into the dead-letter queue: the payload could not be read as a known contract")]
    private static partial void LogPoisonMessage(ILogger logger, string? messageId);

    [LoggerMessage(
        EventId = EventIds.EmailConsumerTransientFailure,
        Level = LogLevel.Warning,
        Message = "Processing message {MessageId} failed with {Error}; requeueing for redelivery {Redelivery} of {DeliveryLimit}")]
    private static partial void LogTransientFailure(ILogger logger, string? messageId, Error error, int redelivery, int deliveryLimit);

    [LoggerMessage(
        EventId = EventIds.EmailConsumerRetriesExhausted,
        Level = LogLevel.Error,
        Message = "Processing message {MessageId} still failed with {Error} after {DeliveryLimit} redeliveries; the broker moves it to the dead-letter queue")]
    private static partial void LogRetriesExhausted(ILogger logger, string? messageId, Error error, int deliveryLimit);

    [LoggerMessage(
        EventId = EventIds.EmailConsumerUnexpectedError,
        Level = LogLevel.Error,
        Message = "Unexpected error while handling message {MessageId}")]
    private static partial void LogUnexpectedError(ILogger logger, Exception exception, string? messageId);

    [LoggerMessage(
        EventId = EventIds.EmailConsumerTeardownWarning,
        Level = LogLevel.Debug,
        Message = "Failed to cleanly close the consumer connection or channel")]
    private static partial void LogTeardownWarning(ILogger logger, Exception exception);
}
