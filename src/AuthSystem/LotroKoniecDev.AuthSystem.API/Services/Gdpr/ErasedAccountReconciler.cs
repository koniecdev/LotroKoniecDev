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
/// Works on every erased account (<see cref="ErasedAccounts"/>), not only the ones a run just erased, so
/// it also catches a message the relay sent after the erasure (ADR-0065).
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
    /// Cuts every sent message of an erased account down to <c>{"IdentityUserId": …}</c>: the outbox keeps
    /// sent rows (ADR-0037 §6), and an e-mail change message carries both addresses.
    /// </summary>
    /// <remarks>
    /// An unsent row is left alone: the consumer would read a cut payload as poison, and the next run cuts
    /// it once sent. A row that is not JSON is skipped, so one bad row cannot stop every later run.
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

        // The LIKE limits JSON parsing to rows that name an erased account. The CASE keeps the jsonb cast
        // behind its validity check, so a row that is not JSON matches nothing.
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
    /// The erasure writes this message itself, so this only finds accounts erased before ADR-0065. A
    /// check in every run, not a migration, so a rollback to a release that cannot route the type only
    /// leaves the rows failed until a release that routes them returns (runbook, "Rolling back past
    /// ADR-0065").
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

        // One row per erased account, so this list stays small.
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
