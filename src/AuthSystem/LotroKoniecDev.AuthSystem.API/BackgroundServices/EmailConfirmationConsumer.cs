using System.Text.Json;
using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Services.Emails;
using LotroKoniecDev.AuthSystem.Infrastructure.Messaging;
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
/// (the processor stays idempotent, see its remarks). The broker being down must never block
/// application startup, so connecting happens here with escalating backoff, and the client's
/// automatic recovery re-attaches the consumer if the connection drops later.
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
    /// Pause before a transient failure is nacked back to the queue. With a prefetch of one the
    /// broker redelivers immediately after the nack, so without this pause a down SMTP relay
    /// would spin the redeliver-fail loop hot; 30 s turns that into a calm retry cadence.
    /// </summary>
    private static readonly TimeSpan TransientFailureDelay = TimeSpan.FromSeconds(30);

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
    /// Handles one delivery end-to-end. Every path must end in exactly one ack or nack — an
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
                // Poison: no amount of redelivery fixes an unreadable payload, so it is dropped
                // loudly instead of requeued into an infinite loop.
                LogPoisonMessage(_logger, delivery.BasicProperties.MessageId);
                await channel.BasicNackAsync(
                    delivery.DeliveryTag,
                    multiple: false,
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

            LogTransientFailure(_logger, delivery.BasicProperties.MessageId, ackDecision.Error.ToString());
            await Task.Delay(TransientFailureDelay, stoppingToken);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, cancellationToken: stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown mid-message: deliberately neither ack nor nack — closing the channel
            // returns the unacked delivery to the queue and the next start picks it up.
        }
        catch (Exception ex)
        {
            LogUnexpectedError(_logger, ex, delivery.BasicProperties.MessageId);
            await TryRequeueAsync(channel, delivery.DeliveryTag, stoppingToken);
        }
    }

    /// <summary>
    /// Best-effort nack for the unexpected-exception path: when even the nack fails (typically a
    /// dead channel), the delivery is unacked anyway and the broker requeues it on channel close.
    /// </summary>
    private async Task TryRequeueAsync(IChannel channel, ulong deliveryTag, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TransientFailureDelay, stoppingToken);
            await channel.BasicNackAsync(deliveryTag, multiple: false, requeue: true, cancellationToken: stoppingToken);
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
        Message = "Dropping poison message {MessageId}: the payload could not be read as a known contract")]
    private static partial void LogPoisonMessage(ILogger logger, string? messageId);

    [LoggerMessage(
        EventId = EventIds.EmailConsumerTransientFailure,
        Level = LogLevel.Warning,
        Message = "Processing message {MessageId} failed with {Error}; requeueing for another attempt")]
    private static partial void LogTransientFailure(ILogger logger, string? messageId, string error);

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
