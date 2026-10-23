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
    /// The full-catalog stream (up to 2M rows from a small container to a remote Postgres) needs more
    /// than the 30 s default (spec 0006). While streaming, Npgsql applies it to each network read, so
    /// it limits one stall, not the whole scan.
    /// </summary>
    private static readonly TimeSpan SourceDigestReadTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Must match <c>TranslationConfiguration</c>, like the <c>COPY</c> list in
    /// <c>BulkTranslationInserter</c> (ADR-0011). The import integration tests fail if it drifts.
    /// </summary>
    private const string SourceDigestQuery =
        """
        SELECT "Id", "FileId", "GossipId", "SourceText", "ArgsOrder", "ArgsId", "Status", "RemovedInVersion", "TranslatedText"
        FROM translation."Translations"
        """;

    public TranslationRepository(ApplicationWriteDbContext db) : base(db)
    {
    }

    /// <summary>
    /// Raw Npgsql, not EF: with retry-on-failure on, EF buffers the whole result set in memory, which
    /// ran out of memory on the 792k-row re-import. So this read gives up the retry. It runs before
    /// any write, so a temporary fault only fails the request and the admin uploads again (spec 0006).
    /// </summary>
    public async IAsyncEnumerable<StoredSourceDigest> StreamSourceDigestsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The write context's own connection, like the COPY inserter.
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
            string? translatedText = reader.IsDBNull(8) ? null : reader.GetString(8);

            yield return new StoredSourceDigest(
                TranslationId.FromValue(reader.GetGuid(0)),
                new FragmentKeyValue(reader.GetInt32(1), reader.GetInt64(2)),
                SourceHash.Compute(text, argsOrder, argsId),
                SourceHash.ComputeEcho(translatedText, argsOrder, argsId),
                Enum.Parse<TranslationStatus>(reader.GetString(6)),
                !reader.IsDBNull(7));
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
