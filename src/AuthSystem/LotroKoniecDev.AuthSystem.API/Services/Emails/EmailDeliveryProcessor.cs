using Microsoft.EntityFrameworkCore;
using Npgsql;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.AuthSystem.Persistence.Inbox;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

/// <summary>
/// The delivery-level wrapper around the per-type <see cref="IEmailMessageProcessor"/>: consults
/// the inbox before doing any work and records the message id after success (ADR-0037). Both real
/// delivery paths — <see cref="BackgroundServices.EmailDispatchConsumer"/> and the integration
/// suite's broker-less bridge — resolve this one component, so the dedup logic cannot drift
/// between them. Type-agnostic on purpose: the inbox stays one undiscriminated table (ADR-0037
/// §5 — message ids are outbox row ids, unique across types).
/// </summary>
/// <remarks>
/// Returns the same ack-decision contract as the processor: success means "ack, drop it from the
/// queue" (processed now, or already processed before), failure means "worth redelivering". The
/// inbox row lands AFTER the send on purpose — recording first would trade duplicate-e-mail risk
/// for lost-e-mail risk (ADR-0037 Decision 2). Database faults deliberately escape as exceptions:
/// the consumer's existing transient path rejects the delivery and the broker's delivery limit
/// bounds the loop (ADR-0037 Decision 4).
/// </remarks>
internal sealed partial class EmailDeliveryProcessor
{
    private readonly AuthDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EmailDeliveryProcessor> _logger;

    public EmailDeliveryProcessor(
        AuthDbContext db,
        TimeProvider timeProvider,
        ILogger<EmailDeliveryProcessor> logger)
    {
        _db = db;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<Result> ProcessOnceAsync(
        IEmailMessageProcessor processor,
        object message,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        bool alreadyProcessed = await _db.InboxMessages
            .AnyAsync(inboxMessage => inboxMessage.MessageId == messageId, cancellationToken);
        if (alreadyProcessed)
        {
            LogDuplicateSkipped(_logger, messageId);
            return Result.Success();
        }

        Result ackDecision = await processor.ProcessAsync(message, cancellationToken);
        if (ackDecision.IsFailure)
        {
            return ackDecision;
        }

        _db.InboxMessages.Add(InboxMessage.Create(messageId, _timeProvider.GetUtcNow()));

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsPrimaryKeyViolation(ex))
        {
            // A concurrent duplicate won the insert race, which means the work is done — same
            // ack decision as a pre-check hit.
            LogInboxRaceLost(_logger, messageId);
        }

        return Result.Success();
    }

    private static bool IsPrimaryKeyViolation(DbUpdateException exception)
    {
        return exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
    }

    [LoggerMessage(
        EventId = EventIds.EmailConsumerDuplicateSkipped,
        Level = LogLevel.Information,
        Message = "Skipping message {MessageId}: the inbox already recorded it as processed")]
    private static partial void LogDuplicateSkipped(ILogger logger, Guid messageId);

    [LoggerMessage(
        EventId = EventIds.EmailConsumerInboxRaceLost,
        Level = LogLevel.Information,
        Message = "Recording message {MessageId} lost an insert race to a concurrent duplicate; acknowledging as processed")]
    private static partial void LogInboxRaceLost(ILogger logger, Guid messageId);
}
