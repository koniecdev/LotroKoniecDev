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
    /// The column list mirrors <c>TranslationConfiguration</c> and the model snapshot exactly. The
    /// binary <c>COPY</c> bypasses the EF mapping, so this list plus the value writes below are the
    /// second place that must track a schema change to <c>Translations</c> (the ADR-0011 trade-off);
    /// the import integration suite asserts the written column state, so any drift fails a test.
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

    public async Task InsertAsync(IReadOnlyCollection<Translation> translations, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(translations);

        if (translations.Count == 0)
        {
            return;
        }

        // The write context's own connection: when the caller has opened a transaction (the import
        // does, via ExecuteInTransactionAsync), it is already open and the COPY joins that transaction.
        NpgsqlConnection connection = (NpgsqlConnection)_dbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using NpgsqlBinaryImporter writer = await connection.BeginBinaryImportAsync(CopyCommand, cancellationToken);

        foreach (Translation translation in translations)
        {
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

        await writer.CompleteAsync(cancellationToken);
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
