using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;
using NpgsqlTypes;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.AuthSystem.Persistence.Outbox;
using LotroKoniecDev.SharedKernel.IntegrationEvents;

namespace LotroKoniecDev.AuthSystem.API.Services.Gdpr;

/// <summary>
/// Keeps the outbox in line with the erased accounts (ADR-0065). It works on every erased account, not
/// only the ones a run has just erased, so it also catches a message the relay sent after the erasure,
/// and every account an earlier version erased. <see cref="ErasedAccounts"/> says which accounts those
/// are.
/// </summary>
internal sealed class ErasedAccountReconciler : IErasedAccountReconciler
{
    /// <summary>
    /// Every outbox contract names its account under this key.
    /// </summary>
    private const string AccountIdKey = nameof(AccountErased.IdentityUserId);

    private readonly AuthDbContext _dbContext;
    private readonly OutboxWriter _outboxWriter;

    public ErasedAccountReconciler(AuthDbContext dbContext, OutboxWriter outboxWriter)
    {
        _dbContext = dbContext;
        _outboxWriter = outboxWriter;
    }

    public async Task<ErasedAccountReconciliation> ReconcileAsync(CancellationToken cancellationToken)
    {
        List<Guid> erasedAccountIds = await ErasedAccounts()
            .Select(account => account.Id)
            .ToListAsync(cancellationToken);

        if (erasedAccountIds.Count == 0)
        {
            return new ErasedAccountReconciliation(MessagesScrubbed: 0, ErasuresAnnounced: 0);
        }

        int scrubbed = await ScrubSentMessagesAsync(erasedAccountIds, cancellationToken);
        int announced = await AnnounceUnannouncedErasuresAsync(cancellationToken);

        return new ErasedAccountReconciliation(scrubbed, announced);
    }

    /// <summary>
    /// Cuts every sent message of an erased account down to <c>{"IdentityUserId": …}</c>. An e-mail
    /// change message carries both addresses, and the outbox keeps every row it has sent (ADR-0037 §6),
    /// so without this those addresses would stay for good. The row keeps its type, its times and the
    /// account id, which is all the diagnostics need.
    /// </summary>
    /// <remarks>
    /// An unsent row is left alone. The consumer would read a cut payload as poison, and deleting the
    /// row could race the relay that is sending it. Once the relay has sent it, the next run cuts it.
    /// The duplicate check of ADR-0037 reads <c>InboxMessages</c>, never the outbox, so a cut row changes
    /// nothing for the consumer.
    /// The payload is <c>text</c>. A row that is not JSON is skipped instead of failing the whole
    /// statement, so one bad row cannot stop every later run.
    /// </remarks>
    private async Task<int> ScrubSentMessagesAsync(List<Guid> erasedAccountIds, CancellationToken cancellationToken)
    {
        IEntityType outboxType = _dbContext.Model.FindEntityType(typeof(OutboxMessage))
                                 ?? throw new InvalidOperationException($"{nameof(OutboxMessage)} is not in the model.");
        string outbox = $"\"{outboxType.GetSchema()}\".\"{outboxType.GetTableName()}\"";
        const string id = nameof(OutboxMessage.Id);
        const string payload = nameof(OutboxMessage.Payload);
        const string processedOn = nameof(OutboxMessage.ProcessedOn);

        // The CASE keeps the jsonb cast behind its validity check, so a row that is not JSON gets a null
        // body and matches nothing. MATERIALIZED makes each payload parse once, in one place.
        string sql = $"""
            WITH sent AS MATERIALIZED (
                SELECT "{id}" AS id,
                       CASE WHEN pg_input_is_valid("{payload}", 'jsonb') THEN "{payload}"::jsonb END AS body
                FROM {outbox}
                WHERE "{processedOn}" IS NOT NULL
            )
            UPDATE {outbox} AS message
            SET "{payload}" = jsonb_build_object('{AccountIdKey}', sent.body -> '{AccountIdKey}')::text
            FROM sent
            WHERE message."{id}" = sent.id
              AND lower(sent.body ->> '{AccountIdKey}') = ANY(@erasedAccountIds)
              AND sent.body <> jsonb_build_object('{AccountIdKey}', sent.body -> '{AccountIdKey}')
            """;

        NpgsqlParameter erasedAccountIdsParameter = new("erasedAccountIds", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = erasedAccountIds.Select(accountId => accountId.ToString()).ToArray()
        };

        return await _dbContext.Database.ExecuteSqlRawAsync(sql, [erasedAccountIdsParameter], cancellationToken);
    }

    /// <summary>
    /// Writes an <see cref="AccountErased"/> for every erased account that has none, so the TMS erases
    /// its copy of the person's name and address. The erasure writes the message in its own save, so
    /// this only finds accounts erased before ADR-0065. It is a check in every run and not a one-off
    /// migration, so the rows only appear once this release runs. A release rolled back to one that
    /// cannot route the type still meets them: its relay marks them failed and leaves them, and they
    /// go out once a release that routes them is back (runbook, "Rolling back past ADR-0065").
    /// </summary>
    private async Task<int> AnnounceUnannouncedErasuresAsync(CancellationToken cancellationToken)
    {
        const string accountErasedType = nameof(AccountErased);

        List<Guid> unannounced = await ErasedAccounts()
            .Where(account => !_dbContext.OutboxMessages.Any(message =>
                message.Type == accountErasedType && message.Payload.Contains(account.Id.ToString())))
            .Select(account => account.Id)
            .ToListAsync(cancellationToken);

        if (unannounced.Count == 0)
        {
            return 0;
        }

        foreach (Guid accountId in unannounced)
        {
            _outboxWriter.Enqueue(new AccountErased(accountId));
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        _outboxWriter.NotifyEnqueuedCommitted();

        return unannounced.Count;
    }

    private IQueryable<ApplicationUser> ErasedAccounts() =>
        _dbContext.Users.Where(Gdpr.ErasedAccounts.Rule);
}
