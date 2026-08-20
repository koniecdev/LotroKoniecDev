using Microsoft.EntityFrameworkCore;
using Npgsql;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.AuthSystem.Persistence.Inbox;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

/// <summary>
/// The wrapper around the per-type <see cref="IEmailMessageProcessor"/>. It checks the inbox before
/// doing any work and writes the message id there after success (ADR-0037).
/// Both delivery paths, <see cref="BackgroundServices.EmailDispatchConsumer"/> and the integration
/// tests' bridge that runs without a broker, use this one component, so the duplicate check cannot
/// differ between them. It knows nothing about message types on purpose: the inbox is one table with
/// no type column (ADR-0037 §5), because message ids are outbox row ids and are unique across types.
/// </summary>
/// <remarks>
/// It returns the same answer as the processor: success means "acknowledge it and drop it from the
/// queue", whether it was processed now or earlier, and failure means "worth sending again".
/// The inbox row is written after the send on purpose. Writing it first would trade the risk of a
/// duplicate e-mail for the risk of losing one (ADR-0037 Decision 2).
/// Database faults are allowed to escape as exceptions: the consumer's normal retry path rejects the
/// delivery and the broker's delivery limit ends the loop (ADR-0037 Decision 4).
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
            // Another delivery of the same message inserted the row first, which means the work is
            // already done. The answer is the same as when the check at the start finds it.
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
