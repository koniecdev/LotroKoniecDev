using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;
using NpgsqlTypes;
using LotroKoniecDev.AuthSystem.API.Outbox;
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
    public async Task<int> ScrubSentMessagesAsync(CancellationToken cancellationToken)
    {
        List<Guid> erasedAccountIds = await ErasedAccountIdsAsync(cancellationToken);
        if (erasedAccountIds.Count == 0)
        {
            return 0;
        }

        IEntityType outboxType = _dbContext.Model.FindEntityType(typeof(OutboxMessage))
                                 ?? throw new InvalidOperationException($"{nameof(OutboxMessage)} is not in the model.");
        string tableName = outboxType.GetTableName()
                           ?? throw new InvalidOperationException($"{nameof(OutboxMessage)} is not mapped to a table.");
        StoreObjectIdentifier table = StoreObjectIdentifier.Table(tableName, outboxType.GetSchema());
        string outbox = $"\"{outboxType.GetSchema()}\".\"{tableName}\"";
        string id = ColumnOf(outboxType, table, nameof(OutboxMessage.Id));
        string type = ColumnOf(outboxType, table, nameof(OutboxMessage.Type));
        string payload = ColumnOf(outboxType, table, nameof(OutboxMessage.Payload));
        string processedOn = ColumnOf(outboxType, table, nameof(OutboxMessage.ProcessedOn));

        // The LIKE keeps the JSON parsing to the few rows that name an erased account; an AccountErased
        // row holds only the id already. The CASE keeps the jsonb cast behind its validity check, so a
        // row that is not JSON gets a null body and matches nothing.
        string sql = $"""
            WITH sent AS MATERIALIZED (
                SELECT "{id}" AS id,
                       CASE WHEN pg_input_is_valid("{payload}", 'jsonb') THEN "{payload}"::jsonb END AS body
                FROM {outbox}
                WHERE "{processedOn}" IS NOT NULL
                  AND "{type}" <> @accountErasedType
                  AND "{payload}" LIKE ANY(@erasedAccountIdPatterns)
            )
            UPDATE {outbox} AS message
            SET "{payload}" = jsonb_build_object('{AccountIdKey}', sent.body -> '{AccountIdKey}')::text
            FROM sent
            WHERE message."{id}" = sent.id
              AND lower(sent.body ->> '{AccountIdKey}') = ANY(@erasedAccountIds)
              AND sent.body <> jsonb_build_object('{AccountIdKey}', sent.body -> '{AccountIdKey}')
            """;

        string[] erasedIdTexts = erasedAccountIds.Select(accountId => accountId.ToString()).ToArray();
        NpgsqlParameter[] parameters =
        [
            new("accountErasedType", nameof(AccountErased)),
            new("erasedAccountIdPatterns", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = erasedIdTexts.Select(idText => $"%{idText}%").ToArray()
            },
            new("erasedAccountIds", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = erasedIdTexts }
        ];

        return await _dbContext.Database.ExecuteSqlRawAsync(sql, parameters, cancellationToken);
    }

    /// <summary>
    /// Writes an <see cref="AccountErased"/> for every erased account that has none, so the TMS erases
    /// its copy of the person's name and address. The erasure writes the message in its own save, so
    /// this only finds accounts erased before ADR-0065. It is a check in every run and not a one-off
    /// migration, so the rows only appear once this release runs. A release rolled back to one that
    /// cannot route the type still meets them: its relay marks them failed and keeps them, and they
    /// go out once a release that routes them is back (runbook, "Rolling back past ADR-0065").
    /// </summary>
    public async Task<int> AnnounceUnannouncedErasuresAsync(CancellationToken cancellationToken)
    {
        if (_dbContext.ChangeTracker.HasChanges())
        {
            throw new InvalidOperationException(
                "The AuthDbContext still tracks changes. Clear it before the announcement, or they are saved with it.");
        }

        List<Guid> erasedAccountIds = await ErasedAccountIdsAsync(cancellationToken);
        if (erasedAccountIds.Count == 0)
        {
            return 0;
        }

        // One row per erased account, so this list stays as small as the list of erased accounts.
        List<string> announcedPayloads = await _dbContext.OutboxMessages
            .Where(message => message.Type == nameof(AccountErased))
            .Select(message => message.Payload)
            .ToListAsync(cancellationToken);
        HashSet<Guid> announced = announcedPayloads
            .Select(ReadAccountId)
            .OfType<Guid>()
            .ToHashSet();

        List<Guid> unannounced = erasedAccountIds
            .Where(accountId => !announced.Contains(accountId))
            .ToList();

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

    private Task<List<Guid>> ErasedAccountIdsAsync(CancellationToken cancellationToken) =>
        _dbContext.Users
            .Where(ErasedAccounts.Rule)
            .Select(account => account.Id)
            .ToListAsync(cancellationToken);

    private static string ColumnOf(IEntityType entityType, StoreObjectIdentifier table, string propertyName) =>
        entityType.FindProperty(propertyName)?.GetColumnName(table)
        ?? throw new InvalidOperationException($"{entityType.DisplayName()}.{propertyName} is not mapped to a column.");

    private static Guid? ReadAccountId(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<AccountErased>(payload)?.IdentityUserId;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
