using Microsoft.EntityFrameworkCore;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.Infrastructure.Messaging;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.AuthSystem.Persistence.Outbox;

namespace LotroKoniecDev.AuthSystem.API.BackgroundServices;

/// <summary>
/// Publishes committed outbox rows to the broker. Signal-driven rather than interval-polled
/// (ADR-0035): writers nudge <see cref="OutboxSignal"/> right after their commit, so the database
/// is only queried moments after work was created — while its compute is already awake — plus one
/// catch-up sweep at startup and a slow safety sweep. A fixed-interval poller would keep the
/// scale-to-zero database awake around the clock for a queue that is almost always empty.
/// </summary>
internal sealed partial class OutboxRelay : BackgroundService
{
    /// <summary>
    /// Upper bound per fetch so a post-outage backlog is drained in slices instead of one list;
    /// the query walks the partial index (<c>IX_OutboxMessages_Unprocessed</c>) top-down.
    /// </summary>
    private const int BatchSize = 100;

    /// <summary>
    /// Ceiling on how long an orphaned row can wait: a commit that raced a crash left no nudge
    /// behind, so a slow sweep re-checks. Six hours of cadence costs ~2.5% of the Neon Free
    /// monthly compute budget (ADR-0035) — tighter bounds buy latency nobody needs.
    /// </summary>
    private static readonly TimeSpan SafetySweepInterval = TimeSpan.FromHours(6);

    /// <summary>
    /// Escalating wait after a failed pass. The ceiling deliberately equals
    /// <see cref="SafetySweepInterval"/>, so a permanently failing row degenerates into the
    /// safety-sweep cadence instead of an around-the-clock retry poll.
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

            // The first pass runs before any wait: the startup catch-up sweep that publishes rows
            // whose nudge died with the previous process.
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
            // Graceful shutdown.
        }
    }

    /// <summary>
    /// Drains every pending row in <see cref="BatchSize"/> slices. Returns <c>false</c> when any
    /// row failed (broker refusal, unroutable type, database fault), so the caller backs off
    /// instead of re-fetching the same failing rows in a tight loop.
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
    /// Publishes one row and saves its outcome immediately: marking per message rather than per
    /// batch narrows the crash window in which an already-published message gets re-published
    /// after a restart (the outbox is at-least-once either way; consumers deduplicate on the
    /// message id).
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
                await _messagePublisher.PublishAsync(routingKey, message.Payload, message.Id, stoppingToken);
                message.MarkAsProcessed(_timeProvider.GetUtcNow());
                published = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // MarkFailed guards against blank input, and Exception.Message is external data —
                // an exception type with an empty message must not blow up the whole pass.
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
