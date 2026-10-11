using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.API.Services.Emails;
using LotroKoniecDev.AuthSystem.Infrastructure.Messaging;
using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Monads;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace LotroKoniecDev.AuthSystem.API.BackgroundServices;

/// <summary>
/// The one e-mail consumer (ADR-0038). It consumes <see cref="RabbitMqTopology.EmailQueue"/> and
/// gives each delivery to the <see cref="IEmailMessageProcessor"/> registered for the AMQP
/// <c>type</c> property, which is the outbox row's <c>Type</c> the publisher put on the wire.
/// The choice is always made on that property and never on the routing key: the type says what the
/// payload is, the routing key says which bindings receive it. That split runs end to end, see
/// <c>OutboxMessageRouting</c>.
/// The broker pushes, we do not poll. Once <c>BasicConsumeAsync</c> has registered the subscription,
/// the broker sends deliveries over the open connection and the client library calls
/// <see cref="OnDeliveredAsync"/> for each one. <see cref="ExecuteAsync"/> only sets this up and then
/// watches that it stays attached until shutdown.
/// </summary>
/// <remarks>
/// We acknowledge by hand (<c>autoAck: false</c>) and only after the processor finished. A crash while
/// sending leaves the delivery unacked, the broker puts it back on the queue, and the next start gets
/// it again. That is at-least-once, the same as the outbox, and the processors stay safe to run twice.
/// On top of that, the inbox drops duplicates by broker message id (ADR-0037), so a message that was
/// already processed is acknowledged without a second e-mail.
/// Failures go three ways (ADR-0036). A payload we can never handle, including one with a missing or
/// unknown message type, goes straight to the dead-letter queue. A temporary failure is put back on
/// the queue after a growing pause. And the broker itself parks a message that uses up
/// <see cref="RabbitMqTopology.EmailDeliveryLimit"/>. So nothing loops forever and nothing is lost in
/// silence.
/// A failed delivery uses <c>basic.reject</c> and never <c>basic.nack</c>. Since RabbitMQ 4.3 only a
/// reject, or a lost connection, raises the <c>x-delivery-count</c> the delivery limit is measured
/// against. A nack with requeue counts as an "explicit return" that the broker redelivers forever
/// without counting.
/// A broker that is down must never stop the application from starting, so we connect here with a
/// growing backoff. The client library's automatic recovery brings back a lost connection, but not a
/// channel the broker closed or a subscription it cancelled, so the consumer checks that it is still
/// attached and attaches again when it is not (#1088). The TMS account event consumer does the same
/// (ADR-0065 §4).
/// </remarks>
internal sealed partial class EmailDispatchConsumer : BackgroundService
{
    internal const string ClientProvidedName = "lotro-auth-api-email-consumer";

    /// <summary>
    /// The growing wait between the first connection attempts. The top of the ladder stays low: the
    /// broker is a container on the same machine and an outage lasts about as long as a deploy. Unlike
    /// the outbox relay, this retry costs no database time, because it only tries a local TCP connect
    /// again.
    /// </summary>
    private static readonly TimeSpan[] ConnectBackoffs =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromMinutes(1)
    ];

    /// <summary>
    /// How long to wait before a temporary failure goes back on the queue, chosen by the broker's
    /// retry count. There is one entry per retry <see cref="RabbitMqTopology.EmailDeliveryLimit"/>
    /// allows, so the two always change together.
    /// With a prefetch of one, the broker redelivers right after the reject. Without a pause, an SMTP
    /// relay that is down would spin that loop and use up the delivery limit in seconds.
    /// The waits grow instead of staying flat, because the whole ladder has to last longer than a
    /// realistic SMTP outage of about 30 minutes before the message ends up in the dead-letter queue.
    /// Waiting in this process blocks the consumer, which does no harm: every message in the queue
    /// needs the same SMTP relay, so none of them could be sent either.
    /// There is a hard limit. The pause holds the delivery unacknowledged, and the broker closes the
    /// channel when an ack takes longer than its consumer timeout, 30 minutes by default. Every entry
    /// must stay well below that.
    /// It is internal so the unit tests can check both rules, one entry per allowed retry and a top
    /// entry under the timeout, instead of trusting this text.
    /// </summary>
    internal static readonly TimeSpan[] RedeliveryBackoffs =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(15)
    ];

    /// <summary>
    /// How often the consumer checks that it is still attached.
    /// </summary>
    private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(30);

    private readonly IConnectionFactory _connectionFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EmailDispatchConsumer> _logger;
    private readonly TimeSpan[] _connectBackoffs;
    private readonly TimeSpan _watchInterval;

    private IConnection? _connection;
    private IChannel? _channel;

    /// <summary>
    /// Reads the broker message id the inbox uses to spot duplicates (ADR-0037). It is internal so the
    /// unit tests can check that a missing id, an id that is not a Guid and an empty Guid all fail.
    /// </summary>
    internal static bool TryReadMessageId(IReadOnlyBasicProperties properties, out Guid messageId)
    {
        return Guid.TryParse(properties.MessageId, out messageId) && messageId != Guid.Empty;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                AsyncEventingBasicConsumer consumer = await AttachConsumerWithRetryAsync(stoppingToken);
                LogStarted(_logger, RabbitMqTopology.EmailQueue);

                // All the work happens in OnDeliveredAsync, on the client library's own loop. This only
                // watches that the subscription is still there, and attaches again when it is not.
                await WaitWhileAttachedAsync(consumer, stoppingToken);

                LogDetached(_logger, RabbitMqTopology.EmailQueue);
                await CloseAsync();
            }
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested)
        {
            // The app is shutting down, which is not an error, whatever the attach in flight threw.
        }
        finally
        {
            await CloseAsync();
        }
    }

    public EmailDispatchConsumer(
        IOptions<RabbitMqOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<EmailDispatchConsumer> logger)
        : this(
            RabbitMqConnectionFactoryBuilder.Build(options.Value, ClientProvidedName),
            scopeFactory,
            logger,
            ConnectBackoffs,
            WatchInterval)
    {
    }

    /// <summary>
    /// Lets a test hand in its own broker client and shorten the pauses and the watch, so it can run the
    /// slow paths in seconds. The container only sees the public constructor.
    /// </summary>
    internal EmailDispatchConsumer(
        IConnectionFactory connectionFactory,
        IServiceScopeFactory scopeFactory,
        ILogger<EmailDispatchConsumer> logger,
        TimeSpan[] connectBackoffs,
        TimeSpan watchInterval)
    {
        ArgumentOutOfRangeException.ThrowIfZero(connectBackoffs.Length);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(watchInterval, TimeSpan.Zero);

        _connectionFactory = connectionFactory;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _connectBackoffs = connectBackoffs;
        _watchInterval = watchInterval;
    }

    /// <summary>
    /// Connects, declares the topology and registers the consumer, all as one attempt that either
    /// fully succeeds or fully fails.
    /// The whole attach sits inside the retry loop on purpose. Any exception leaving
    /// <see cref="ExecuteAsync"/> while the host is not stopping would stop the entire host
    /// (<see cref="BackgroundServiceExceptionBehavior.StopHost"/>). A failure between the connect and
    /// the consume registration, such as a topology mismatch or a channel closed in between, must only
    /// make e-mail delivery worse and must never take login down with it.
    /// So every failure is retried here, a timeout the client reports as a cancellation included. Only
    /// the host's own stop token ends the loop (#1088).
    /// A failed attempt disposes whatever it managed to open before it waits, so retrying cannot leave
    /// half-attached connections behind, which automatic recovery would otherwise keep alive forever.
    /// </summary>
    private async Task<AsyncEventingBasicConsumer> AttachConsumerWithRetryAsync(CancellationToken stoppingToken)
    {
        int failedAttempts = 0;

        while (true)
        {
            try
            {
                _connection = await _connectionFactory.CreateConnectionAsync(stoppingToken);
                IChannel channel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);
                _channel = channel;
                await RabbitMqTopologyDeclaration.DeclareAsync(channel, stoppingToken);

                // Prefetch 1: the broker sends the next message only after the previous one was
                // acknowledged, so a backlog never floods this process and e-mails go out one by one.
                await channel.BasicQosAsync(
                    prefetchSize: 0,
                    prefetchCount: 1,
                    global: false,
                    cancellationToken: stoppingToken);

                AsyncEventingBasicConsumer consumer = new(channel);
                consumer.ReceivedAsync += (_, delivery) => OnDeliveredAsync(channel, delivery, stoppingToken);

                await channel.BasicConsumeAsync(
                    queue: RabbitMqTopology.EmailQueue,
                    autoAck: false,
                    consumer: consumer,
                    cancellationToken: stoppingToken);

                return consumer;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                await CloseAsync();
                failedAttempts++;
                TimeSpan wait = _connectBackoffs[Math.Min(failedAttempts - 1, _connectBackoffs.Length - 1)];
                LogConnectFailed(_logger, ex, wait.TotalSeconds);
                await Task.Delay(wait, stoppingToken);
            }
        }
    }

    /// <summary>
    /// Returns once the consumer has been detached for two looks in a row. The first look gives
    /// automatic recovery its chance after a lost connection.
    /// </summary>
    private async Task WaitWhileAttachedAsync(AsyncEventingBasicConsumer consumer, CancellationToken stoppingToken)
    {
        int detachedLooks = 0;

        while (detachedLooks < 2)
        {
            await Task.Delay(_watchInterval, stoppingToken);

            bool attached = _channel is { IsOpen: true } && consumer.IsRunning;
            detachedLooks = attached ? 0 : detachedLooks + 1;
        }
    }

    /// <summary>
    /// Handles one delivery from start to finish. Every path has to end in exactly one ack or reject.
    /// An exception leaving this handler is swallowed by the client library, and the delivery then
    /// stays unacknowledged and stuck until the channel dies.
    /// </summary>
    private async Task OnDeliveredAsync(
        IChannel channel,
        BasicDeliverEventArgs delivery,
        CancellationToken stoppingToken)
    {
        try
        {
            if (!TryReadMessageId(delivery.BasicProperties, out Guid messageId))
            {
                // We can never handle this one. Without a usable message id we cannot tell a duplicate
                // from a new message, and handling it anyway would reopen the duplicate problem the
                // inbox solves (ADR-0037). So it waits in the dead-letter queue for a person.
                LogMessageIdUnusable(_logger, delivery.BasicProperties.MessageId);
                await channel.BasicRejectAsync(
                    delivery.DeliveryTag,
                    requeue: false,
                    cancellationToken: stoppingToken);
                return;
            }

            Result ackDecision;
            await using (AsyncServiceScope scope = _scopeFactory.CreateAsyncScope())
            {
                string? messageType = delivery.BasicProperties.Type;
                IEmailMessageProcessor? processor = string.IsNullOrWhiteSpace(messageType)
                    ? null
                    : scope.ServiceProvider.GetKeyedService<IEmailMessageProcessor>(messageType);
                if (processor is null)
                {
                    // We can never handle this one. With a missing or unknown type there is no
                    // processor for this delivery, and sending it again would not change that, so it
                    // waits for a person (ADR-0038), like a message with an unusable id.
                    LogUnknownMessageType(_logger, delivery.BasicProperties.MessageId, messageType);
                    await channel.BasicRejectAsync(
                        delivery.DeliveryTag,
                        requeue: false,
                        cancellationToken: stoppingToken);
                    return;
                }

                object? message = processor.TryDeserialize(delivery.Body.Span);
                if (message is null)
                {
                    // We can never handle this one. Sending an unreadable payload again would not
                    // help, so it is rejected at once and the broker moves it to the dead-letter queue
                    // for a person.
                    LogPoisonMessage(_logger, delivery.BasicProperties.MessageId);
                    await channel.BasicRejectAsync(
                        delivery.DeliveryTag,
                        requeue: false,
                        cancellationToken: stoppingToken);
                    return;
                }

                EmailDeliveryProcessor deliveryProcessor =
                    scope.ServiceProvider.GetRequiredService<EmailDeliveryProcessor>();
                ackDecision = await deliveryProcessor.ProcessOnceAsync(
                    processor, message, messageId, stoppingToken);
            }

            if (ackDecision.IsSuccess)
            {
                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
                return;
            }

            int redeliveries = RedeliveryCount.Read(delivery.BasicProperties.Headers);
            if (redeliveries >= RabbitMqTopology.EmailDeliveryLimit)
            {
                // This is the last attempt. The reject takes the count past the delivery limit, so the
                // broker moves the message to the dead-letter queue instead of sending it again. No
                // pause is needed.
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
            // We are shutting down in the middle of a message. We deliberately neither ack nor
            // reject: closing the channel puts the delivery back on the queue and the next start
            // handles it.
        }
        catch (Exception ex)
        {
            LogUnexpectedError(_logger, ex, delivery.BasicProperties.MessageId);
            await TryRequeueAsync(channel, delivery.DeliveryTag, stoppingToken);
        }
    }

    /// <summary>
    /// Tries to reject the delivery after an unexpected exception. If even the reject fails, usually
    /// because the channel is dead, the delivery is unacknowledged anyway and the broker puts it back
    /// when the channel closes. That lost connection also raises the delivery count, so even a crash
    /// loop ends at the delivery limit.
    /// The pause is always the first step of the ladder and does not grow with the retry count. An
    /// unexpected exception tells us nothing about the SMTP relay, so this only slows a hot loop down.
    /// A processor that keeps throwing uses up the delivery limit in minutes and parks, which is
    /// exactly the bound we want.
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
            // We are shutting down during the pause. The unacknowledged delivery goes back on the
            // queue when the channel closes.
        }
        catch (Exception ex)
        {
            LogUnexpectedError(_logger, ex, messageId: null);
        }
    }

    private async Task CloseAsync()
    {
        IChannel? channel = _channel;
        _channel = null;

        if (channel is not null)
        {
            try
            {
                await channel.DisposeAsync();
            }
            catch (Exception ex)
            {
                LogTeardownWarning(_logger, ex);
            }
        }

        IConnection? connection = _connection;
        _connection = null;

        if (connection is not null)
        {
            try
            {
                await connection.DisposeAsync();
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
        EventId = EventIds.EmailConsumerDetached,
        Level = LogLevel.Warning,
        Message = "The e-mail consumer is no longer attached to queue {Queue}: its channel is closed or its subscription is gone. Attaching again.")]
    private static partial void LogDetached(ILogger logger, string queue);

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
        EventId = EventIds.EmailConsumerMessageIdUnusable,
        Level = LogLevel.Error,
        Message = "Rejecting message with unusable message id {MessageId} into the dead-letter queue: the inbox cannot deduplicate a delivery without an id")]
    private static partial void LogMessageIdUnusable(ILogger logger, string? messageId);

    [LoggerMessage(
        EventId = EventIds.EmailConsumerUnknownMessageType,
        Level = LogLevel.Error,
        Message = "Rejecting message {MessageId} into the dead-letter queue: no processor is registered for message type {MessageType}")]
    private static partial void LogUnknownMessageType(ILogger logger, string? messageId, string? messageType);

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
