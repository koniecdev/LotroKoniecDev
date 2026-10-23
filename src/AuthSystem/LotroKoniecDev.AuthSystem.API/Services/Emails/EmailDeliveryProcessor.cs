using Microsoft.EntityFrameworkCore;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.AuthSystem.Persistence.Inbox;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

/// <summary>
/// Checks the inbox before any work and records the message id after success (ADR-0037). The consumer
/// and the integration tests' broker-free bridge both use it, so the duplicate check cannot differ.
/// Message ids are outbox row ids, unique across types, so the inbox has no type column (ADR-0037 §5).
/// </summary>
/// <remarks>
/// The inbox row is written after the send: writing it first would trade a duplicate e-mail for a lost
/// one (ADR-0037 Decision 2). Database faults escape as exceptions, and the broker's delivery limit ends
/// the retries (ADR-0037 Decision 4).
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
        catch (DbUpdateException ex) when (IsInboxKeyViolation(ex))
        {
            // Another delivery of the same message recorded it first, so the work is already done.
            LogInboxRaceLost(_logger, messageId);
        }

        return Result.Success();
    }

    /// <summary>
    /// Only a duplicate on the inbox key proves another delivery recorded this message first; any other
    /// error escapes as a fault.
    /// </summary>
    private bool IsInboxKeyViolation(DbUpdateException exception) =>
        exception.ViolatedUniqueConstraint is { } constraint
        && string.Equals(
            constraint,
            _db.Model.FindEntityType(typeof(InboxMessage))?.FindPrimaryKey()?.GetName(),
            StringComparison.Ordinal);

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
