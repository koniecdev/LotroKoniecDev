using Microsoft.EntityFrameworkCore;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.Infrastructure.Messaging;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.AuthSystem.Persistence.Outbox;

namespace LotroKoniecDev.AuthSystem.API.BackgroundServices;

/// <summary>
/// Works on a signal, not a timer (ADR-0035): a commit that carries an outbox row wakes
/// <see cref="OutboxSignal"/> (<see cref="OutboxWakeUpInterceptor"/>), so the database is queried while
/// it is still awake. A timer would keep a scale-to-zero database awake all day for an almost empty
/// queue. A catch-up sweep at startup and a slow safety sweep cover a lost signal.
/// </summary>
internal sealed partial class OutboxRelay : BackgroundService
{
    /// <summary>
    /// A backlog after an outage is handled in pieces. The query reads the partial index
    /// <c>IX_OutboxMessages_Unprocessed</c> from the top.
    /// </summary>
    private const int BatchSize = 100;

    /// <summary>
    /// The longest a row can wait after its signal was lost in a crash. Every six hours costs about 2.5%
    /// of the Neon Free monthly compute budget (ADR-0035).
    /// </summary>
    private static readonly TimeSpan SafetySweepInterval = TimeSpan.FromHours(6);

    /// <summary>
    /// The longest wait equals <see cref="SafetySweepInterval"/>, so a row that always fails is retried at
    /// the safety-sweep pace instead of polled all day.
    /// </summary>
    private static readonly TimeSpan[] RetryBackoffs =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(6)
    ];

    private readonly IMessagePublisher _messagePublisher;
    private readonly OutboxSignal _outboxSignal;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OutboxRelay> _logger;

    public OutboxRelay(
        IMessagePublisher messagePublisher,
        OutboxSignal outboxSignal,
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<OutboxRelay> logger)
    {
        _messagePublisher = messagePublisher;
        _outboxSignal = outboxSignal;
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            int failedPasses = 0;

            // The first pass, before any wait, is the startup catch-up sweep.
            while (!stoppingToken.IsCancellationRequested)
            {
                bool passWasClean = await ProcessPendingAsync(stoppingToken);
                failedPasses = passWasClean ? 0 : failedPasses + 1;

                TimeSpan maxWait = passWasClean
                    ? SafetySweepInterval
                    : RetryBackoffs[Math.Min(failedPasses - 1, RetryBackoffs.Length - 1)];

                await _outboxSignal.WaitAsync(maxWait, _timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // The app is shutting down, which is not an error.
        }
    }

    /// <summary>
    /// <c>false</c> when any row failed, so the caller waits instead of retrying at once.
    /// </summary>
    /// <remarks>
    /// A row refused on its own (no route, or no queue bound, such as <c>AccountErased</c> before the TMS
    /// binds, ADR-0065) is skipped for the rest of the pass, or such rows would fill every batch and hold
    /// back newer e-mail. Any other failure ends the pass: a down broker would refuse every row alike.
    /// </remarks>
    private async Task<bool> ProcessPendingAsync(CancellationToken stoppingToken)
    {
        try
        {
            List<Guid> failedThisPass = [];

            while (true)
            {
                await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
                AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

                List<OutboxMessage> batch = await db.OutboxMessages
                    .Where(message => message.ProcessedOn == null && !failedThisPass.Contains(message.Id))
                    .OrderBy(message => message.OccurredOn)
                    .Take(BatchSize)
                    .ToListAsync(stoppingToken);

                foreach (OutboxMessage message in batch)
                {
                    PublishOutcome outcome = await PublishOneAsync(db, message, stoppingToken);
                    if (outcome is PublishOutcome.BrokerFailed)
                    {
                        return false;
                    }

                    if (outcome is PublishOutcome.MessageRefused)
                    {
                        failedThisPass.Add(message.Id);
                    }
                }

                if (batch.Count < BatchSize)
                {
                    return failedThisPass.Count == 0;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogRelayPassFailed(_logger, ex);
            return false;
        }
    }

    /// <summary>
    /// Saves each row at once, which shortens the window in which a crash republishes a sent message. It
    /// is at-least-once either way: the e-mail consumer drops duplicates, and the TMS consumer's work is
    /// safe to repeat (ADR-0065).
    /// </summary>
    private async Task<PublishOutcome> PublishOneAsync(
        AuthDbContext db,
        OutboxMessage message,
        CancellationToken stoppingToken)
    {
        PublishOutcome outcome;

        if (!OutboxMessageRouting.TryGetRoute(message.Type, out OutboxRoute? route))
        {
            message.MarkFailed($"No routing key is mapped for outbox message type '{message.Type}'.");
            LogMessageUnroutable(_logger, message.Id, message.Type);
            outcome = PublishOutcome.MessageRefused;
        }
        else
        {
            try
            {
                await _messagePublisher.PublishAsync(
                    route.Exchange, route.RoutingKey, message.Type, message.Payload, message.Id, stoppingToken);
                message.MarkAsProcessed(_timeProvider.GetUtcNow());
                outcome = PublishOutcome.Published;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // MarkFailed rejects a blank value, and an exception from outside may have an empty message.
                string error = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
                message.MarkFailed(error);
                LogMessagePublishFailed(_logger, ex, message.Id, message.Type, message.Attempts);
                outcome = ex is MessageNotRoutedException ? PublishOutcome.MessageRefused : PublishOutcome.BrokerFailed;
            }
        }

        await db.SaveChangesAsync(stoppingToken);
        return outcome;
    }

    [LoggerMessage(
        EventId = EventIds.OutboxRelayPassFailed,
        Level = LogLevel.Error,
        Message = "Outbox relay pass failed before reaching individual messages. Will retry with backoff.")]
    private static partial void LogRelayPassFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = EventIds.OutboxMessagePublishFailed,
        Level = LogLevel.Warning,
        Message = "Publishing outbox message {MessageId} of type {MessageType} failed (attempt {Attempts}). Will retry with backoff.")]
    private static partial void LogMessagePublishFailed(
        ILogger logger,
        Exception exception,
        Guid messageId,
        string messageType,
        int attempts);

    [LoggerMessage(
        EventId = EventIds.OutboxMessageUnroutable,
        Level = LogLevel.Error,
        Message = "Outbox message {MessageId} carries type {MessageType} with no mapped routing key; it stays unprocessed until a mapping ships.")]
    private static partial void LogMessageUnroutable(ILogger logger, Guid messageId, string messageType);

    private enum PublishOutcome
    {
        Published,

        /// <summary>
        /// This one message cannot go: no route is mapped, or no queue is bound to its key.
        /// </summary>
        MessageRefused,

        /// <summary>
        /// Every other message would fail the same way.
        /// </summary>
        BrokerFailed
    }
}
