using System.Data;
using System.Runtime.CompilerServices;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Repositories;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LotroKoniecDev.TranslationSystem.Persistence.DomainRepositories;

internal sealed class TranslationRepository : GenericRepository<Translation, TranslationId>, ITranslationRepository
{
    /// <summary>
    /// The projection read streams the whole catalog (~800k rows today, ~2M design horizon) on a
    /// 0.25 vCPU container against a remote Postgres, so it gets its own generous per-command
    /// ceiling instead of the context's 30 s default (spec 0006). Npgsql applies the value per
    /// network read while streaming, so this bounds a stall, not the total scan.
    /// </summary>
    private static readonly TimeSpan SourceDigestReadTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Mirrors <c>TranslationConfiguration</c>'s column names like the ADR-0011 <c>COPY</c> list in
    /// <c>BulkTranslationInserter</c> does — the second place that must track a schema change to
    /// these columns; the import integration suite fails on any drift.
    /// </summary>
    private const string SourceDigestQuery =
        """
        SELECT "Id", "FileId", "GossipId", "SourceText", "ArgsOrder", "ArgsId", "Status", "RemovedInVersion"
        FROM translation."Translations"
        """;

    /// <summary>
    /// The bootstrap-seed sibling of <see cref="SourceDigestQuery"/> (PERF-06): it drops the source
    /// triple and reads the current Polish text instead, so the seed can decide the idempotent
    /// already-approved case from memory. Same schema-drift caveat — the column list must track
    /// <c>TranslationConfiguration</c>.
    /// </summary>
    private const string CatalogEntryQuery =
        """
        SELECT "Id", "FileId", "GossipId", "Status", "TranslatedText", "RemovedInVersion"
        FROM translation."Translations"
        """;

    public TranslationRepository(ApplicationWriteDbContext db) : base(db)
    {
    }

    /// <summary>
    /// Deliberately raw Npgsql, not an EF query: with retry-on-failure enabled EF wraps every
    /// result set in its <c>BufferedDataReader</c>, materializing all rows before yielding the
    /// first — the memory-gate harness OOM'd exactly there on the 792k-row re-import. Streaming
    /// and transparent retry are mutually exclusive, so this read forfeits the retry: it runs in
    /// Pass 1 before any write, so a transient fault just fails the request and the admin
    /// re-uploads (spec 0006).
    /// </summary>
    public async IAsyncEnumerable<StoredSourceDigest> StreamSourceDigestsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The write context's own connection, like the COPY inserter — open it if EF has not yet.
        NpgsqlConnection connection = (NpgsqlConnection)DbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using NpgsqlCommand command = new(SourceDigestQuery, connection);
        command.CommandTimeout = (int)SourceDigestReadTimeout.TotalSeconds;

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            string text = reader.GetString(3);
            string? argsOrder = reader.IsDBNull(4) ? null : reader.GetString(4);
            string? argsId = reader.IsDBNull(5) ? null : reader.GetString(5);

            yield return new StoredSourceDigest(
                TranslationId.FromValue(reader.GetGuid(0)),
                new FragmentKeyValue(reader.GetInt32(1), reader.GetInt64(2)),
                SourceHash.Compute(text, argsOrder, argsId),
                Enum.Parse<TranslationStatus>(reader.GetString(6)),
                !reader.IsDBNull(7));
        }
    }

    /// <summary>
    /// The bootstrap-seed twin of <see cref="StreamSourceDigestsAsync"/> (PERF-06): same raw-Npgsql
    /// streaming — buffered-reader retry would materialize the whole catalog and OOM the constrained
    /// bootstrap container — reading the current Polish text and status the merge decides on rather
    /// than the source hash. It forfeits retry for the same reason: it runs before any write, so a
    /// transient fault just fails the one-time seed and the next start retries it (idempotent).
    /// </summary>
    public async IAsyncEnumerable<StoredTranslationEntry> StreamCatalogEntriesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = (NpgsqlConnection)DbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using NpgsqlCommand command = new(CatalogEntryQuery, connection);
        command.CommandTimeout = (int)SourceDigestReadTimeout.TotalSeconds;

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            yield return new StoredTranslationEntry(
                TranslationId.FromValue(reader.GetGuid(0)),
                new FragmentKeyValue(reader.GetInt32(1), reader.GetInt64(2)),
                Enum.Parse<TranslationStatus>(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                !reader.IsDBNull(5));
        }
    }

    public async Task<IReadOnlyList<Translation>> GetByIdsAsync(
        IReadOnlyList<TranslationId> ids,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count == 0)
        {
            return [];
        }

        List<Translation> translations = await DbContext.Set<Translation>()
            .Where(translation => ids.Contains(translation.Id))
            .ToListAsync(cancellationToken);

        return translations;
    }

    public async Task<Maybe<Translation>> GetByFragmentKeyAsync(FragmentKey fragmentKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fragmentKey);

        Translation? translation = await DbContext.Set<Translation>()
            .FirstOrDefaultAsync(
                row => row.FragmentKey.FileId == fragmentKey.FileId && row.FragmentKey.GossipId == fragmentKey.GossipId,
                cancellationToken);

        return Maybe<Translation>.From(translation);
    }

    public void InsertRange(IEnumerable<Translation> translations)
    {
        ArgumentNullException.ThrowIfNull(translations);

        DbContext.Set<Translation>().AddRange(translations);
    }

    public async Task<bool> AnyReferencesGameVersionAsync(GameVersionId gameVersionId, CancellationToken cancellationToken)
    {
        bool referenced = await DbContext.Set<Translation>()
            .AnyAsync(
                translation => translation.IntroducedInVersion == gameVersionId
                    || translation.LastSourceChangeInVersion == gameVersionId
                    || translation.RemovedInVersion == gameVersionId,
                cancellationToken);

        return referenced;
    }
}
