using System.Data.Common;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage;
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
/// Erases the translator profile of every account the AuthSystem erases (ADR-0065). It mirrors the
/// AuthSystem's <c>EmailDispatchConsumer</c>: a manual ack only after the work is saved, and a broker
/// that is down never stops the API from starting.
/// </summary>
/// <remarks>
/// No inbox: erasing a profile twice gives the same result as once. A message is settled one of four
/// ways:
/// <list type="bullet">
/// <item>Erased: <c>basic.ack</c>.</item>
/// <item>Never handleable (unknown type, unreadable payload, refused command): <c>basic.reject</c>
/// without requeue, straight to the dead-letter queue (ADR-0036).</item>
/// <item>Database unavailable: <c>basic.nack</c> after a growing pause, because giving up would leave
/// the person's name in the TMS for good. Since RabbitMQ 4.3 a nack does not count against
/// <see cref="AccountEventsTopology.ErasedDeliveryLimit"/>, so the message outlasts any outage.</item>
/// <item>Any other exception: <c>basic.reject</c> with requeue, which counts, so a message that always
/// fails ends in the dead-letter queue instead of looping.</item>
/// </list>
/// </remarks>
internal sealed partial class AccountErasedConsumer : BackgroundService
{
    /// <summary>
    /// Internal so the unit tests can check which pause follows each failed attempt.
    /// </summary>
    internal static readonly TimeSpan[] ConnectBackoffs =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromMinutes(1)
    ];

    /// <summary>
    /// Indexed by failed attempts in a row; the last entry repeats for as long as the outage lasts. Every
    /// entry must stay well under the broker's 30-minute consumer timeout, because the pause holds the
    /// delivery unacknowledged. Internal so the unit tests can check that rule.
    /// </summary>
    internal static readonly TimeSpan[] RetryBackoffs =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(15)
    ];

    /// <summary>
    /// Automatic recovery brings back a lost connection, but not a channel the broker closed or a
    /// subscription it cancelled (after the queue was deleted to change its arguments, for example).
    /// </summary>
    internal static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(30);

    private readonly IConnectionFactory _connectionFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AccountErasedConsumer> _logger;
    private readonly TimeSpan[] _connectBackoffs;
    private readonly TimeSpan[] _retryBackoffs;
    private readonly TimeSpan _watchInterval;

    private IConnection? _connection;
    private IChannel? _channel;

    /// <summary>
    /// Prefetch is 1, so deliveries are handled one at a time and every message waits on the same
    /// database. A count for the consumer is the count for the message at the head of the queue.
    /// </summary>
    private int _failedAttemptsInARow;

    /// <summary>
    /// <c>null</c> when the payload can never be handled.
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

    /// <summary>
    /// Any database error counts, not only the transient ones: the erasure reads one profile by id and
    /// writes fixed values, so a rotated password, a lost grant or a missing table is the environment,
    /// never the message. Anything else is taken to fail the same way every time.
    /// </summary>
    internal static bool IsDatabaseUnavailable(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbException
                or RetryLimitExceededException
                or TimeoutException
                or SocketException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Internal so the brokered integration host connects the way production does.
    /// </summary>
    internal static ConnectionFactory BuildConnectionFactory(RabbitMqSettings settings) =>
        new()
        {
            HostName = settings.Host,
            Port = settings.Port,
            UserName = settings.Username,
            Password = settings.Password,
            VirtualHost = settings.VirtualHost,
            ClientProvidedName = "lotro-tms-api-account-consumer",
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true
        };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                AsyncEventingBasicConsumer consumer = await AttachConsumerWithRetryAsync(stoppingToken);
                LogStarted(_logger, AccountEventsTopology.ErasedQueue);

                // The work happens in OnDeliveredAsync on the client library's loop; this only watches.
                await WaitWhileAttachedAsync(consumer, stoppingToken);

                LogDetached(_logger, AccountEventsTopology.ErasedQueue);
                await CloseAsync();
            }
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down is not an error, whatever the attach in flight threw.
        }
        finally
        {
            await CloseAsync();
        }
    }

    /// <summary>
    /// No exception may leave this handler: the client library swallows it and the delivery would stay
    /// stuck until the channel dies. On shutdown, or when even the reject fails, the broker puts the
    /// delivery back when the channel closes.
    /// </summary>
    internal async Task OnDeliveredAsync(
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

            Result result;
            try
            {
                result = await EraseAsync(message, stoppingToken);
            }
            catch (Exception ex) when (IsDatabaseUnavailable(ex) && !stoppingToken.IsCancellationRequested)
            {
                await ReturnForLaterAsync(channel, delivery.DeliveryTag, message, messageId, ex, stoppingToken);
                return;
            }

            _failedAttemptsInARow = 0;

            if (result.IsFailure)
            {
                LogRefused(_logger, message.IdentityUserId, messageId, result.Error.Message);
                await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, cancellationToken: stoppingToken);
                return;
            }

            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
            LogProfileErased(_logger, message.IdentityUserId, messageId);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Neither ack nor reject: closing the channel puts the delivery back for the next start.
        }
        catch (Exception ex)
        {
            LogUnexpectedError(_logger, ex, messageId);
            await TryRequeueAsync(channel, delivery.DeliveryTag, stoppingToken);
        }
    }

    public AccountErasedConsumer(
        IOptions<RabbitMqSettings> options,
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<AccountErasedConsumer> logger)
        : this(
            BuildConnectionFactory(options.Value),
            scopeFactory,
            timeProvider,
            logger,
            ConnectBackoffs,
            RetryBackoffs,
            WatchInterval)
    {
    }

    /// <summary>
    /// For tests: their own broker client and shorter pauses. Every pause runs on
    /// <paramref name="timeProvider"/>, so a test can also move a fake clock through them (#1223).
    /// </summary>
    internal AccountErasedConsumer(
        IConnectionFactory connectionFactory,
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<AccountErasedConsumer> logger,
        TimeSpan[] connectBackoffs,
        TimeSpan[] retryBackoffs,
        TimeSpan watchInterval)
    {
        ArgumentOutOfRangeException.ThrowIfZero(connectBackoffs.Length);
        ArgumentOutOfRangeException.ThrowIfLessThan(connectBackoffs.Min(), TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfZero(retryBackoffs.Length);
        ArgumentOutOfRangeException.ThrowIfLessThan(retryBackoffs.Min(), TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(watchInterval, TimeSpan.Zero);

        _connectionFactory = connectionFactory;
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
        _connectBackoffs = connectBackoffs;
        _retryBackoffs = retryBackoffs;
        _watchInterval = watchInterval;
    }

    /// <summary>
    /// An exception leaving <see cref="ExecuteAsync"/> would stop the whole host, and a broker problem
    /// must never take the API down, so every failure is retried here, a client timeout reported as a
    /// cancellation included. A failed attempt disposes what it opened.
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

                return consumer;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                await CloseAsync();
                failedAttempts++;
                TimeSpan wait = _connectBackoffs[Math.Min(failedAttempts - 1, _connectBackoffs.Length - 1)];
                LogConnectFailed(_logger, ex, wait.TotalSeconds);
                await Task.Delay(wait, _timeProvider, stoppingToken);
            }
        }
    }

    /// <summary>
    /// Returns after two detached looks in a row: the first gives automatic recovery its chance.
    /// </summary>
    private async Task WaitWhileAttachedAsync(AsyncEventingBasicConsumer consumer, CancellationToken stoppingToken)
    {
        int detachedLooks = 0;

        while (detachedLooks < 2)
        {
            await Task.Delay(_watchInterval, _timeProvider, stoppingToken);

            bool attached = _channel is { IsOpen: true } && consumer.IsRunning;
            detachedLooks = attached ? 0 : detachedLooks + 1;
        }
    }

    private async Task<Result> EraseAsync(AccountErased message, CancellationToken stoppingToken)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        ICommandHandler<EraseTranslatorProfile.Command, Result> handler = scope.ServiceProvider
            .GetRequiredService<ICommandHandler<EraseTranslatorProfile.Command, Result>>();

        return await handler.Handle(
            new EraseTranslatorProfile.Command(IdentityId.FromValue(message.IdentityUserId)),
            stoppingToken);
    }

    /// <summary>
    /// A nack, which the delivery limit does not count.
    /// </summary>
    private async Task ReturnForLaterAsync(
        IChannel channel,
        ulong deliveryTag,
        AccountErased message,
        string? messageId,
        Exception failure,
        CancellationToken stoppingToken)
    {
        _failedAttemptsInARow++;
        int rung = Math.Min(_failedAttemptsInARow - 1, _retryBackoffs.Length - 1);

        // Once the pauses stop growing, the outage is long enough for a person to look.
        LogLevel level = rung == _retryBackoffs.Length - 1 ? LogLevel.Error : LogLevel.Warning;
        LogDatabaseUnavailable(_logger, level, failure, message.IdentityUserId, messageId, _failedAttemptsInARow, _retryBackoffs[rung].TotalMinutes);

        await Task.Delay(_retryBackoffs[rung], _timeProvider, stoppingToken);
        await channel.BasicNackAsync(deliveryTag, multiple: false, requeue: true, cancellationToken: stoppingToken);
    }

    /// <summary>
    /// A reject, which the delivery limit counts, so a message that always fails ends in the dead-letter
    /// queue. If even the reject fails, the channel is most likely dead and the broker puts the delivery
    /// back when it closes.
    /// </summary>
    private async Task TryRequeueAsync(IChannel channel, ulong deliveryTag, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(_retryBackoffs[0], _timeProvider, stoppingToken);
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
        EventId = EventIds.AccountConsumerDetached,
        Level = LogLevel.Warning,
        Message = "The account event consumer is no longer attached to queue {Queue}: its channel is closed or its subscription is gone. Attaching again.")]
    private static partial void LogDetached(ILogger logger, string queue);

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
        EventId = EventIds.AccountConsumerRefused,
        Level = LogLevel.Error,
        Message = "Rejecting message {MessageId} for user {UserId} into the dead-letter queue: erasing the translator profile was refused: {Reason}")]
    private static partial void LogRefused(ILogger logger, Guid userId, string? messageId, string reason);

    [LoggerMessage(
        EventId = EventIds.AccountConsumerEraseFailed,
        Message = "Erasing the translator profile of user {UserId} (message {MessageId}) failed because the database is unavailable, attempt {Attempt} in a row; it goes back on the queue after {PauseMinutes} minute(s), and the person's name stays in the TMS until an attempt succeeds")]
    private static partial void LogDatabaseUnavailable(ILogger logger, LogLevel level, Exception exception, Guid userId, string? messageId, int attempt, double pauseMinutes);

    [LoggerMessage(
        EventId = EventIds.AccountConsumerUnexpectedError,
        Level = LogLevel.Error,
        Message = "Unexpected error while handling account event {MessageId}; it goes back on the queue, and the broker parks it once it keeps failing")]
    private static partial void LogUnexpectedError(ILogger logger, Exception exception, string? messageId);

    [LoggerMessage(
        EventId = EventIds.AccountConsumerTeardownWarning,
        Level = LogLevel.Debug,
        Message = "Failed to cleanly close the account event consumer's connection or channel")]
    private static partial void LogTeardownWarning(ILogger logger, Exception exception);
}
