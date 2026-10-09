using System.Text.Json;
using Microsoft.Extensions.Options;
using LotroKoniecDev.SharedKernel.IntegrationEvents;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.API.Features.Translators;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace LotroKoniecDev.TranslationSystem.API.Messaging;

/// <summary>
/// Erases the translator profile of every account the AuthSystem erases (ADR-0065). It consumes
/// <see cref="AccountEventsTopology.ErasedQueue"/> and hands each <see cref="AccountErased"/> to
/// <see cref="EraseTranslatorProfile"/>.
/// It mirrors the AuthSystem's <c>EmailDispatchConsumer</c>: the broker pushes, we acknowledge by hand
/// and only after the work is saved, and a broker that is down never stops the API from starting.
/// </summary>
/// <remarks>
/// It needs no inbox. Erasing a profile twice gives the same profile as erasing it once, so a
/// redelivered or republished message does no harm.
/// A message we can never handle, of an unknown type or with an unreadable payload, goes straight to
/// the dead-letter queue with <c>basic.reject</c>, as in the AuthSystem (ADR-0036).
/// A database failure is different from the e-mail consumer's SMTP failure: giving up on it would leave
/// the person's name in the TMS for good, and nothing would ever send the message again. So it goes back
/// on the queue with <c>basic.nack</c> after a growing pause. Since RabbitMQ 4.3 a nack does not count
/// against <see cref="AccountEventsTopology.ErasedDeliveryLimit"/>, so the message waits out an outage
/// of any length and is never parked for it.
/// </remarks>
internal sealed partial class AccountErasedConsumer : BackgroundService
{
    private static readonly TimeSpan[] ConnectBackoffs =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromMinutes(1)
    ];

    /// <summary>
    /// The pause before a message the database refused goes back on the queue, chosen by how many
    /// attempts in a row have failed. The last entry repeats for as long as the outage lasts. Every
    /// entry stays well under the broker's 30-minute consumer timeout, because the pause holds the
    /// delivery unacknowledged. It is internal so the unit tests can check that rule.
    /// </summary>
    internal static readonly TimeSpan[] RetryBackoffs =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(15)
    ];

    private readonly RabbitMqSettings _settings;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AccountErasedConsumer> _logger;

    private IConnection? _connection;
    private IChannel? _channel;

    /// <summary>
    /// Prefetch is 1, so deliveries are handled one at a time and every message waits on the same
    /// database. A count for the consumer is the count for the message at the head of the queue.
    /// </summary>
    private int _failedAttemptsInARow;

    public AccountErasedConsumer(
        IOptions<RabbitMqSettings> options,
        IServiceScopeFactory scopeFactory,
        ILogger<AccountErasedConsumer> logger)
    {
        _settings = options.Value;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Reads the payload, or returns <c>null</c> when it can never be handled. It is internal so the
    /// unit tests can check every unreadable shape.
    /// </summary>
    internal static AccountErased? TryDeserialize(ReadOnlySpan<byte> body)
    {
        try
        {
            AccountErased? message = JsonSerializer.Deserialize<AccountErased>(body);
            return message is null || message.IdentityUserId == Guid.Empty ? null : message;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await AttachConsumerWithRetryAsync(stoppingToken);

            LogStarted(_logger, AccountEventsTopology.ErasedQueue);

            // The work happens in OnDeliveredAsync, on the client library's own loop. This task only
            // keeps the service, and with it the channel, alive until shutdown.
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // The app is shutting down, which is not an error.
        }
        finally
        {
            await CloseAsync();
        }
    }

    /// <summary>
    /// Connects, declares the topology and registers the consumer as one attempt. Any exception
    /// leaving <see cref="ExecuteAsync"/> would stop the whole host, and a broker problem must never
    /// take the translation API down, so every failure is retried here after a pause. A failed attempt
    /// disposes what it opened first, so no half-attached connection is left behind.
    /// </summary>
    private async Task AttachConsumerWithRetryAsync(CancellationToken stoppingToken)
    {
        int failedAttempts = 0;

        while (true)
        {
            try
            {
                ConnectionFactory connectionFactory = new()
                {
                    HostName = _settings.Host,
                    Port = _settings.Port,
                    UserName = _settings.Username,
                    Password = _settings.Password,
                    VirtualHost = _settings.VirtualHost,
                    ClientProvidedName = "lotro-tms-api-account-consumer",
                    AutomaticRecoveryEnabled = true,
                    TopologyRecoveryEnabled = true
                };

                _connection = await connectionFactory.CreateConnectionAsync(stoppingToken);
                IChannel channel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);
                _channel = channel;
                await AccountEventsTopology.DeclareAsync(channel, stoppingToken);

                await channel.BasicQosAsync(
                    prefetchSize: 0,
                    prefetchCount: 1,
                    global: false,
                    cancellationToken: stoppingToken);

                AsyncEventingBasicConsumer consumer = new(channel);
                consumer.ReceivedAsync += (_, delivery) => OnDeliveredAsync(channel, delivery, stoppingToken);

                await channel.BasicConsumeAsync(
                    queue: AccountEventsTopology.ErasedQueue,
                    autoAck: false,
                    consumer: consumer,
                    cancellationToken: stoppingToken);

                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await CloseAsync();
                failedAttempts++;
                TimeSpan wait = ConnectBackoffs[Math.Min(failedAttempts - 1, ConnectBackoffs.Length - 1)];
                LogConnectFailed(_logger, ex, wait.TotalSeconds);
                await Task.Delay(wait, stoppingToken);
            }
        }
    }

    /// <summary>
    /// Handles one delivery. Every path ends in exactly one ack or reject: an exception leaving this
    /// handler is swallowed by the client library, and the delivery would stay stuck until the channel
    /// dies.
    /// </summary>
    private async Task OnDeliveredAsync(
        IChannel channel,
        BasicDeliverEventArgs delivery,
        CancellationToken stoppingToken)
    {
        string? messageId = delivery.BasicProperties.MessageId;

        try
        {
            string? messageType = delivery.BasicProperties.Type;
            if (!string.Equals(messageType, nameof(AccountErased), StringComparison.Ordinal))
            {
                LogUnknownMessageType(_logger, messageId, messageType);
                await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, cancellationToken: stoppingToken);
                return;
            }

            AccountErased? message = TryDeserialize(delivery.Body.Span);
            if (message is null)
            {
                LogPoisonMessage(_logger, messageId);
                await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, cancellationToken: stoppingToken);
                return;
            }

            Exception? failure = await TryEraseAsync(message, stoppingToken);
            if (failure is null)
            {
                _failedAttemptsInARow = 0;
                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
                LogProfileErased(_logger, message.IdentityUserId, messageId);
                return;
            }

            _failedAttemptsInARow++;
            int rung = Math.Min(_failedAttemptsInARow - 1, RetryBackoffs.Length - 1);

            // Once the pauses stop growing, the outage is long enough for a person to look.
            LogLevel level = rung == RetryBackoffs.Length - 1 ? LogLevel.Error : LogLevel.Warning;
            LogEraseFailed(_logger, level, failure, message.IdentityUserId, messageId, _failedAttemptsInARow, RetryBackoffs[rung].TotalMinutes);

            await Task.Delay(RetryBackoffs[rung], stoppingToken);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, cancellationToken: stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down in the middle of a message. Neither ack nor reject: closing the channel puts
            // the delivery back on the queue, and the next start handles it.
        }
        catch (Exception ex)
        {
            LogUnexpectedError(_logger, ex, messageId);
            await TryRequeueAsync(channel, delivery.DeliveryTag, stoppingToken);
        }
    }

    /// <summary>
    /// Returns <c>null</c> once the profile is erased, or the reason it could not be. The command fails
    /// only on an empty id, which <see cref="TryDeserialize"/> has already refused, so in practice the
    /// reason is a database error: worth another try.
    /// </summary>
    private async Task<Exception?> TryEraseAsync(AccountErased message, CancellationToken stoppingToken)
    {
        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
            ICommandHandler<EraseTranslatorProfile.Command, Result> handler = scope.ServiceProvider
                .GetRequiredService<ICommandHandler<EraseTranslatorProfile.Command, Result>>();

            Result result = await handler.Handle(
                new EraseTranslatorProfile.Command(IdentityId.FromValue(message.IdentityUserId)),
                stoppingToken);

            return result.IsSuccess
                ? null
                : new InvalidOperationException(result.Error.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            return ex;
        }
    }

    /// <summary>
    /// Tries to put the delivery back after an unexpected exception. A reject, so a message that keeps
    /// breaking the consumer itself ends in the dead-letter queue at the delivery limit instead of
    /// looping. If even the reject fails, the channel is most likely dead, and the broker puts the
    /// unacknowledged delivery back when it closes.
    /// </summary>
    private async Task TryRequeueAsync(IChannel channel, ulong deliveryTag, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(RetryBackoffs[0], stoppingToken);
            await channel.BasicRejectAsync(deliveryTag, requeue: true, cancellationToken: stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Shutting down during the pause. The delivery goes back when the channel closes.
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
        EventId = EventIds.AccountConsumerStarted,
        Level = LogLevel.Information,
        Message = "Consuming account events from queue {Queue}")]
    private static partial void LogStarted(ILogger logger, string queue);

    [LoggerMessage(
        EventId = EventIds.AccountConsumerConnectFailed,
        Level = LogLevel.Warning,
        Message = "Connecting the account event consumer to the broker failed; retrying in {DelaySeconds}s")]
    private static partial void LogConnectFailed(ILogger logger, Exception exception, double delaySeconds);

    [LoggerMessage(
        EventId = EventIds.TranslatorProfileErased,
        Level = LogLevel.Information,
        Message = "Erased the translator profile of user {UserId}, if there was one (message {MessageId})")]
    private static partial void LogProfileErased(ILogger logger, Guid userId, string? messageId);

    [LoggerMessage(
        EventId = EventIds.AccountConsumerUnknownMessageType,
        Level = LogLevel.Error,
        Message = "Rejecting message {MessageId} into the dead-letter queue: the account event consumer does not handle message type {MessageType}")]
    private static partial void LogUnknownMessageType(ILogger logger, string? messageId, string? messageType);

    [LoggerMessage(
        EventId = EventIds.AccountConsumerPoisonMessage,
        Level = LogLevel.Error,
        Message = "Rejecting poison message {MessageId} into the dead-letter queue: the payload is not a readable AccountErased")]
    private static partial void LogPoisonMessage(ILogger logger, string? messageId);

    [LoggerMessage(
        EventId = EventIds.AccountConsumerEraseFailed,
        Message = "Erasing the translator profile of user {UserId} (message {MessageId}) failed, attempt {Attempt} in a row; it goes back on the queue after {PauseMinutes} minute(s), and the person's name stays in the TMS until an attempt succeeds")]
    private static partial void LogEraseFailed(ILogger logger, LogLevel level, Exception exception, Guid userId, string? messageId, int attempt, double pauseMinutes);

    [LoggerMessage(
        EventId = EventIds.AccountConsumerUnexpectedError,
        Level = LogLevel.Error,
        Message = "Unexpected error while handling account event {MessageId}")]
    private static partial void LogUnexpectedError(ILogger logger, Exception exception, string? messageId);

    [LoggerMessage(
        EventId = EventIds.AccountConsumerTeardownWarning,
        Level = LogLevel.Debug,
        Message = "Failed to cleanly close the account event consumer's connection or channel")]
    private static partial void LogTeardownWarning(ILogger logger, Exception exception);
}
