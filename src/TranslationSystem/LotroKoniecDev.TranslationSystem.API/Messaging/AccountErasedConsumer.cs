using System.Net.Sockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;
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
/// A message is settled one of four ways:
/// <list type="bullet">
/// <item>Erased: <c>basic.ack</c>.</item>
/// <item>One we can never handle (an unknown type, an unreadable payload, a command the handler
/// refuses): <c>basic.reject</c> without requeue, so it goes straight to the dead-letter queue, as in
/// the AuthSystem (ADR-0036).</item>
/// <item>The database is unavailable: <c>basic.nack</c> after a growing pause. Giving up on it would
/// leave the person's name in the TMS for good, and nothing would ever send the message again. Since
/// RabbitMQ 4.3 a nack does not count against <see cref="AccountEventsTopology.ErasedDeliveryLimit"/>,
/// so the message waits out an outage of any length.</item>
/// <item>Any other exception, such as a bug: <c>basic.reject</c> with requeue, which counts, so a
/// message that keeps failing for good ends in the dead-letter queue at the limit instead of looping.</item>
/// </list>
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
    /// The pause before a message goes back on the queue after the database was unavailable, chosen by
    /// how many attempts in a row have failed. The last entry repeats for as long as the outage lasts.
    /// Every entry stays well under the broker's 30-minute consumer timeout, because the pause holds the
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

    /// <summary>
    /// How often the consumer checks that it is still attached. Automatic recovery brings back a lost
    /// connection, but not a channel the broker closed or a subscription it cancelled, for example
    /// after the queue was deleted to change its arguments.
    /// </summary>
    private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(30);

    private readonly RabbitMqSettings _settings;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AccountErasedConsumer> _logger;
    private readonly TimeSpan[] _retryBackoffs;
    private readonly TimeSpan _watchInterval;

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
        : this(options, scopeFactory, logger, RetryBackoffs, WatchInterval)
    {
    }

    /// <summary>
    /// Lets a test shorten the pauses and the watch, so it can run the slow paths in seconds. The
    /// container only sees the public constructor.
    /// </summary>
    internal AccountErasedConsumer(
        IOptions<RabbitMqSettings> options,
        IServiceScopeFactory scopeFactory,
        ILogger<AccountErasedConsumer> logger,
        TimeSpan[] retryBackoffs,
        TimeSpan watchInterval)
    {
        ArgumentOutOfRangeException.ThrowIfZero(retryBackoffs.Length);

        _settings = options.Value;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _retryBackoffs = retryBackoffs;
        _watchInterval = watchInterval;
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

    /// <summary>
    /// Says whether a failure means the database could not be reached, which only time can fix. EF has
    /// already retried a transient error a few times when it gives up with
    /// <see cref="RetryLimitExceededException"/>. Anything else is taken to fail the same way every
    /// time. It is internal so the unit tests can check the line between the two.
    /// </summary>
    internal static bool IsDatabaseUnavailable(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is RetryLimitExceededException
                or TimeoutException
                or SocketException
                or NpgsqlException { IsTransient: true })
            {
                return true;
            }
        }

        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                AsyncEventingBasicConsumer consumer = await AttachConsumerWithRetryAsync(stoppingToken);
                LogStarted(_logger, AccountEventsTopology.ErasedQueue);

                // The work happens in OnDeliveredAsync, on the client library's own loop. This only
                // watches that the subscription is still there, and attaches again when it is not.
                await WaitWhileAttachedAsync(consumer, stoppingToken);

                LogDetached(_logger, AccountEventsTopology.ErasedQueue);
                await CloseAsync();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
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
    /// take the translation API down, so every failure is retried here after a pause, a timeout the
    /// client reports as a cancellation included. A failed attempt disposes what it opened first, so no
    /// half-attached connection is left behind.
    /// </summary>
    private async Task<AsyncEventingBasicConsumer> AttachConsumerWithRetryAsync(CancellationToken stoppingToken)
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

                return consumer;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
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
    /// Handles one delivery. Every path ends in one ack, nack or reject, except a shutdown or a channel
    /// that refuses even the reject: then the broker puts the delivery back when the channel closes. No
    /// exception may leave this handler, because the client library swallows it and the delivery would
    /// stay stuck until the channel dies. It is internal so the unit tests can drive each path.
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
            // Shutting down in the middle of a message. Neither ack nor reject: closing the channel puts
            // the delivery back on the queue, and the next start handles it.
        }
        catch (Exception ex)
        {
            LogUnexpectedError(_logger, ex, messageId);
            await TryRequeueAsync(channel, delivery.DeliveryTag, stoppingToken);
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
    /// Pauses, then returns the message with a nack, which the delivery limit does not count.
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

        await Task.Delay(_retryBackoffs[rung], stoppingToken);
        await channel.BasicNackAsync(deliveryTag, multiple: false, requeue: true, cancellationToken: stoppingToken);
    }

    /// <summary>
    /// Tries to put the delivery back after an exception that is not a database outage. A reject, so a
    /// message that keeps failing for good ends in the dead-letter queue at the delivery limit instead
    /// of looping. If even the reject fails, the channel is most likely dead, and the broker puts the
    /// unacknowledged delivery back when it closes.
    /// </summary>
    private async Task TryRequeueAsync(IChannel channel, ulong deliveryTag, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(_retryBackoffs[0], stoppingToken);
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
        Message = "The account event consumer is no longer attached to queue {Queue}: the broker closed its channel or cancelled its subscription. Attaching again.")]
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
