using System.Text;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Application.Abstractions.Messaging;
using LotroKoniecDev.Application.Extensions;
using LotroKoniecDev.Application.Parsers;
using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Models;
using LotroKoniecDev.Primitives.Constants;
using LotroKoniecDev.Primitives.Enums;

namespace LotroKoniecDev.Application.Features.Exporting;

internal sealed class ExportTextsQueryHandler : IQueryHandler<ExportTextsQuery, Result<ExportSummaryResponse>>
{
    private const int ProgressReportInterval = 500;
    private readonly IDatFileHandler _datFileHandler;
    private readonly IProgress<OperationProgress> _progressReporter;

    public ExportTextsQueryHandler(
        IDatFileHandler datFileHandler,
        IProgress<OperationProgress> progressReporter)
    {
        _datFileHandler = datFileHandler;
        _progressReporter = progressReporter;
    }

    public async ValueTask<Result<ExportSummaryResponse>> Handle(ExportTextsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (string.IsNullOrWhiteSpace(query.DatFilePath))
        {
            return Result.Failure<ExportSummaryResponse>(
                Error.Validation($"{nameof(ExportTextsQuery)}.Validation", "DatFilePath must not be empty."));
        }

        if (string.IsNullOrWhiteSpace(query.OutputPath))
        {
            return Result.Failure<ExportSummaryResponse>(
                Error.Validation($"{nameof(ExportTextsQuery)}.Validation", "OutputPath must not be empty."));
        }

        Result<int> openResult = _datFileHandler.Open(query.DatFilePath, DatFileAccess.Read);
        if (openResult.IsFailure)
        {
            return Result.Failure<ExportSummaryResponse>(openResult.Error);
        }

        int handle = openResult.Value;

        try
        {
            Dictionary<int, (int Size, int Iteration)> fileSizes = _datFileHandler.GetAllSubfileSizes(handle);
            int totalTextFiles = fileSizes.Count(kvp => SubFile.IsTextFile(kvp.Key));

            await using StreamWriter writer = new(query.OutputPath, append: false, Encoding.UTF8);
            await WriteHeaderAsync(writer);

            int processedFiles = 0;
            int totalFragments = 0;

            foreach ((int fileId, (int size, _)) in fileSizes)
            {
                if (!SubFile.IsTextFile(fileId))
                {
                    continue;
                }

                Result<SubFile> loadResult = _datFileHandler.LoadSubFile(handle, fileId, size);
                if (loadResult.IsSuccess)
                {
                    SubFile subFile = loadResult.Value;
                    int fragmentCount = 0;

                    foreach ((ulong fragmentId, Fragment fragment) in subFile.Fragments)
                    {
                        string text = string.Join(DatFileConstants.PieceSeparator, fragment.Pieces);

                        // Generate default args_order and args_id if fragment has arguments
                        string argsOrder = "NULL";
                        string argsId = "NULL";

                        if (fragment.HasArguments)
                        {
                            IEnumerable<string> order = Enumerable
                                .Range(1, fragment.ArgRefs.Count)
                                .Select(x => x.ToString());

                            argsOrder = string.Join("-", order);
                            argsId = argsOrder; // Default: same order
                        }

                        await writer.WriteLineAsync(
                            FormatRow(fileId, fragmentId, text, argsOrder, argsId, fragment.ArgRefs.Count));

                        fragmentCount++;
                    }

                    totalFragments += fragmentCount;
                }

                processedFiles++;

                if (processedFiles % ProgressReportInterval == 0)
                {
                    _progressReporter.Report(new OperationProgress(processedFiles, totalTextFiles));
                }
            }

            return Result.Success(new ExportSummaryResponse(
                processedFiles,
                totalFragments,
                query.OutputPath));
        }
        catch (Exception ex)
        {
            return Result.Failure<ExportSummaryResponse>(
                DomainErrors.Export.CannotCreateOutputFile(query.OutputPath, ex.Message));
        }
        finally
        {
            _datFileHandler.Close(handle);
        }

    }

    /// <summary>
    /// Composes one <c>exported.txt</c> row. The escape is applied here (ADR-0039), on the joined
    /// text — the piece separator carries nothing escapable, so folding after the join leaves it
    /// intact. A row is composed rather than interpolated inline so the file's format has a seam a
    /// test can hold: the handler streams straight to disk, and a unit suite must not assert on real
    /// file output.
    /// </summary>
    /// <remarks>
    /// The trailing <c>source_digest</c> (ADR-0047 §2) is the digest of the very triple this row
    /// carries, so a translator who edits <c>exported.txt</c> by hand still ends up with a patchable
    /// file, and the TMS import can verify the column against its own <c>SourceHash</c> and fail
    /// loudly on drift instead of quietly shipping a digest players' patchers reject. Composed
    /// through the same <see cref="SourceDigest"/> the write guard uses, so export and guard cannot
    /// disagree about what the fragment's export form is.
    /// </remarks>
    internal static string FormatRow(int fileId, ulong fragmentId, string text, string argsOrder, string argsId, int argumentCount)
        => $"{fileId}||{fragmentId}||{TranslationLineEscaper.Escape(text)}||{argsOrder}||{argsId}||1||{SourceDigest.ForExportForm(text, argumentCount)}";

    private static async Task WriteHeaderAsync(StreamWriter writer)
    {
        await writer.WriteLineAsync("# LOTRO Text Export - Ready for Translation");
        await writer.WriteLineAsync("# Format: file_id||gossip_id||text||args_order||args_id||approved||source_digest");
        await writer.WriteLineAsync("#");
        await writer.WriteLineAsync("# Translation instructions:");
        await writer.WriteLineAsync("#   1. Replace English text with Polish translation");
        await writer.WriteLineAsync("#   2. DO NOT modify <--DO_NOT_TOUCH!--> markers - they are variable placeholders");
        await writer.WriteLineAsync("#   3. args_order/args_id - leave as NULL unless changing argument order");
        await writer.WriteLineAsync("#   4. Remove lines you don't translate (or leave them - identical lines are ignored)");
        await writer.WriteLineAsync("#   5. DO NOT touch source_digest - it identifies the English this row was translated");
        await writer.WriteLineAsync("#      from. A row without it is never written into the DAT (ADR-0047)");
        await writer.WriteLineAsync("#");
    }
}
