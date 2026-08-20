using System.Data;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace LotroKoniecDev.TranslationSystem.Persistence.Bulk;

internal sealed class BulkTranslationInserter : IBulkTranslationInserter
{
    /// <summary>
    /// This column list must match <c>TranslationConfiguration</c> and the model snapshot exactly.
    /// The binary <c>COPY</c> goes around the EF mapping, so this list and the value writes below are
    /// a second place that has to follow every schema change to <c>Translations</c>. That is the
    /// trade-off ADR-0011 accepted. The import integration tests check the written columns, so a
    /// mismatch fails a test.
    /// </summary>
    private const string CopyCommand =
        """
        COPY translation."Translations" (
            "Id", "FileId", "GossipId", "SourceText", "ArgsOrder", "ArgsId", "Status",
            "TranslatedText", "PreviousSourceText", "SubmittedById", "ApprovedById",
            "IntroducedInVersion", "LastSourceChangeInVersion", "RemovedInVersion",
            "CreatedAt", "UpdatedAt")
        FROM STDIN (FORMAT BINARY)
        """;

    private readonly ApplicationWriteDbContext _dbContext;

    public BulkTranslationInserter(ApplicationWriteDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task InsertAsync(IAsyncEnumerable<Translation> translations, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(translations);

        // The COPY opens on the first row, not before: an empty stream must do nothing, and the only
        // way to know a stream is empty is to enumerate it. While the COPY is open the connection is
        // in CopyIn state, so the producing stream must not use the database. The import's producer
        // reads the buffered upload file instead. Disposing without CompleteAsync aborts the COPY, so
        // a producer that fails halfway leaves no partial rows behind.
        NpgsqlBinaryImporter? writer = null;
        try
        {
            await foreach (Translation translation in translations.WithCancellation(cancellationToken))
            {
                if (writer is null)
                {
                    // The write context's own connection. When the caller already opened a
                    // transaction, as the import does through ExecuteInTransactionAsync, the
                    // connection is open and the COPY joins that transaction.
                    NpgsqlConnection connection = (NpgsqlConnection)_dbContext.Database.GetDbConnection();
                    if (connection.State != ConnectionState.Open)
                    {
                        await connection.OpenAsync(cancellationToken);
                    }

                    writer = await connection.BeginBinaryImportAsync(CopyCommand, cancellationToken);
                }

                await writer.StartRowAsync(cancellationToken);

                await writer.WriteAsync(translation.Id.Value, NpgsqlDbType.Uuid, cancellationToken);
                await writer.WriteAsync(translation.FragmentKey.FileId, NpgsqlDbType.Integer, cancellationToken);
                await writer.WriteAsync(translation.FragmentKey.GossipId, NpgsqlDbType.Bigint, cancellationToken);
                await writer.WriteAsync(translation.Source.Text, NpgsqlDbType.Text, cancellationToken);
                await WriteNullableTextAsync(writer, translation.Source.ArgsOrder, cancellationToken);
                await WriteNullableTextAsync(writer, translation.Source.ArgsId, cancellationToken);
                await writer.WriteAsync(translation.Status.ToString(), NpgsqlDbType.Varchar, cancellationToken);
                await WriteNullableTextAsync(writer, translation.TranslatedText, cancellationToken);
                await WriteNullableTextAsync(writer, translation.PreviousSourceText, cancellationToken);
                await WriteNullableUuidAsync(writer, translation.SubmittedById?.Value, cancellationToken);
                await WriteNullableUuidAsync(writer, translation.ApprovedById?.Value, cancellationToken);
                await writer.WriteAsync(translation.IntroducedInVersion.Value, NpgsqlDbType.Uuid, cancellationToken);
                await WriteNullableUuidAsync(writer, translation.LastSourceChangeInVersion?.Value, cancellationToken);
                await WriteNullableUuidAsync(writer, translation.RemovedInVersion?.Value, cancellationToken);
                await writer.WriteAsync(translation.CreatedAt.UtcDateTime, NpgsqlDbType.TimestampTz, cancellationToken);
                await writer.WriteAsync(translation.UpdatedAt.UtcDateTime, NpgsqlDbType.TimestampTz, cancellationToken);
            }

            if (writer is not null)
            {
                await writer.CompleteAsync(cancellationToken);
            }
        }
        finally
        {
            if (writer is not null)
            {
                await writer.DisposeAsync();
            }
        }
    }

    private static async ValueTask WriteNullableTextAsync(NpgsqlBinaryImporter writer, string? value, CancellationToken cancellationToken)
    {
        if (value is null)
        {
            await writer.WriteNullAsync(cancellationToken);
            return;
        }

        await writer.WriteAsync(value, NpgsqlDbType.Text, cancellationToken);
    }

    private static async ValueTask WriteNullableUuidAsync(NpgsqlBinaryImporter writer, Guid? value, CancellationToken cancellationToken)
    {
        if (value is null)
        {
            await writer.WriteNullAsync(cancellationToken);
            return;
        }

        await writer.WriteAsync(value.Value, NpgsqlDbType.Uuid, cancellationToken);
    }
}
