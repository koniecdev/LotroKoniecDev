using Microsoft.EntityFrameworkCore;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.Infrastructure.Messaging;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.AuthSystem.Persistence.Outbox;

namespace LotroKoniecDev.AuthSystem.API.BackgroundServices;

/// <summary>
/// Publishes committed outbox rows to the broker. It works on a signal instead of a timer (ADR-0035):
/// writers wake <see cref="OutboxSignal"/> right after their commit, so the database is queried only
/// moments after the work appeared, while it is still awake. On top of that there is one catch-up
/// sweep at startup and a slow safety sweep.
/// A timer would keep a database that scales to zero awake all day for a queue that is almost always
/// empty.
/// </summary>
internal sealed partial class OutboxRelay : BackgroundService
{
    /// <summary>
    /// How many rows one fetch takes, so a backlog after an outage is handled in pieces instead of one
    /// huge list. The query reads the partial index <c>IX_OutboxMessages_Unprocessed</c> from the top.
    /// </summary>
    private const int BatchSize = 100;

    /// <summary>
    /// The longest a forgotten row can wait. A commit that happened just before a crash left no signal
    /// behind, so a slow sweep looks again. Running it every six hours costs about 2.5% of the Neon
    /// Free monthly compute budget (ADR-0035), and a shorter interval would only buy speed nobody
    /// needs.
    /// </summary>
    private static readonly TimeSpan SafetySweepInterval = TimeSpan.FromHours(6);

    /// <summary>
    /// The growing wait after a failed pass. The longest wait equals <see cref="SafetySweepInterval"/>
    /// on purpose, so a row that always fails ends up retried at the safety-sweep pace instead of
    /// being polled all day.
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

            // The first pass runs before any wait. It is the catch-up sweep at startup, which
            // publishes rows whose signal was lost when the previous process ended.
            while (!stoppingToken.IsCancellationRequested)
            {
                bool passWasClean = await ProcessPendingAsync(stoppingToken);
                failedPasses = passWasClean ? 0 : failedPasses + 1;

                TimeSpan maxWait = passWasClean
                    ? SafetySweepInterval
                    : RetryBackoffs[Math.Min(failedPasses - 1, RetryBackoffs.Length - 1)];

                await _outboxSignal.WaitAsync(maxWait, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // The app is shutting down, which is not an error.
        }
    }

    /// <summary>
    /// Works through every pending row, <see cref="BatchSize"/> at a time. It returns <c>false</c> when
    /// any row failed, whether the broker refused it, nothing is bound to its key, or the database
    /// failed. The caller then waits instead of fetching the same failing rows again at once.
    /// </summary>
    private async Task<bool> ProcessPendingAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
                AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

                List<OutboxMessage> batch = await db.OutboxMessages
                    .Where(message => message.ProcessedOn == null)
                    .OrderBy(message => message.OccurredOn)
                    .Take(BatchSize)
                    .ToListAsync(stoppingToken);

                if (batch.Count == 0)
                {
                    return true;
                }

                bool anyFailed = false;

                foreach (OutboxMessage message in batch)
                {
                    anyFailed |= !await PublishOneAsync(db, message, stoppingToken);
                }

                if (anyFailed)
                {
                    return false;
                }

                if (batch.Count < BatchSize)
                {
                    return true;
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
    /// Publishes one row and saves the result at once. Marking each message instead of a whole batch
    /// shortens the window in which a crash makes us publish an already published message again after
    /// a restart. The outbox is at-least-once either way, and consumers drop duplicates by message id.
    /// </summary>
    private async Task<bool> PublishOneAsync(
        AuthDbContext db,
        OutboxMessage message,
        CancellationToken stoppingToken)
    {
        bool published;

        if (!OutboxMessageRouting.TryGetRoutingKey(message.Type, out string? routingKey))
        {
            message.MarkFailed($"No routing key is mapped for outbox message type '{message.Type}'.");
            LogMessageUnroutable(_logger, message.Id, message.Type);
            published = false;
        }
        else
        {
            try
            {
                await _messagePublisher.PublishAsync(
                    routingKey, message.Type, message.Payload, message.Id, stoppingToken);
                message.MarkAsProcessed(_timeProvider.GetUtcNow());
                published = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // MarkFailed rejects a blank value, and Exception.Message comes from outside our code.
                // An exception type with an empty message must not break the whole pass.
                string error = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
                message.MarkFailed(error);
                LogMessagePublishFailed(_logger, ex, message.Id, message.Type, message.Attempts);
                published = false;
            }
        }

        await db.SaveChangesAsync(stoppingToken);
        return published;
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
}
