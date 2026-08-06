using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Application.Extensions;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Models;
using LotroKoniecDev.Primitives.Constants;
using LotroKoniecDev.Primitives.Enums;

namespace LotroKoniecDev.Application.Features.Patching;

internal sealed class PatchingService : IPatchingService
{
    private const int ProgressReportInterval = 1000;

    private readonly IDatFileHandler _datFileHandler;
    private readonly ITranslationParser _translationParser;

    public PatchingService(
        IDatFileHandler datFileHandler,
        ITranslationParser translationParser)
    {
        _datFileHandler = datFileHandler;
        _translationParser = translationParser;
    }

    public Result<PatchSummaryResponse> ApplyTranslations(
        string translationsPath,
        string datFilePath,
        IProgress<OperationProgress>? progress = null)
    {
        Result<TranslationParseResult> translationParseResult =
            _translationParser.ParseFile(translationsPath);

        if (translationParseResult.IsFailure)
        {
            return Result.Failure<PatchSummaryResponse>(translationParseResult.Error);
        }

        IReadOnlyList<Translation> translations = translationParseResult.Value.Translations;

        // A line the parser rejected is reported, never swallowed (ADR-0042): its warning rides
        // along with the per-fragment ones into the summary the CLI prints.
        IReadOnlyList<string> parseWarnings = translationParseResult.Value.Warnings;

        if (translations.Count == 0)
        {
            // A file whose every line was rejected must say WHY. The warning list is only printed on
            // a successful patch, so on this path the diagnosis has to ride in the error itself —
            // otherwise the fix of ADR-0042 would stop exactly where it matters most.
            return Result.Failure<PatchSummaryResponse>(
                translationParseResult.Value.RejectedLineCount > 0
                    ? DomainErrors.Translation.NoTranslationsEveryLineRejected(
                        translationParseResult.Value.RejectedLineCount,
                        parseWarnings[0])
                    : DomainErrors.Translation.NoTranslations);
        }

        Result<int> datFileOpenResult = _datFileHandler.Open(datFilePath, DatFileAccess.ReadWrite);

        if (datFileOpenResult.IsFailure)
        {
            return Result.Failure<PatchSummaryResponse>(datFileOpenResult.Error);
        }

        int datFileHandle = datFileOpenResult.Value;

        try
        {
            Dictionary<int, (int Size, int Iteration)> fileSizes = _datFileHandler.GetAllSubfileSizes(datFileHandle);

            List<string> warnings = [.. parseWarnings];
            int appliedCount = 0;
            int skippedCount = 0;

            int currentFileId = -1;
            SubFile? currentSubFile = null;

            foreach (Translation translation in translations)
            {
                if (!translation.IsApproved)
                {
                    skippedCount++;
                    continue;
                }

                if (!fileSizes.ContainsKey(translation.FileId))
                {
                    warnings.Add($"File {translation.FileId} not found in DAT archive");
                    skippedCount++;
                    continue;
                }

                if (!SubFile.IsTextFile(translation.FileId))
                {
                    warnings.Add($"File {translation.FileId} is not a text file");
                    skippedCount++;
                    continue;
                }

                // Screened here — before a subfile is loaded, and well before one is mutated — because
                // this is the last point at which an unwritable row is still just a row (#598, ADR-0043).
                // A hand-edited or hostile file is the only source: the TMS caps the text at the API.
                string[] pieces = translation.GetPieces();

                if (!pieces.All(Fragment.IsWritablePiece))
                {
                    warnings.Add(
                        $"Fragment {translation.GossipId} in file {translation.FileId} has a text piece of "
                        + $"{pieces.Max(piece => piece.Length)} characters, above the "
                        + $"{DatFileConstants.MaxTextPieceLength} the DAT format allows");
                    skippedCount++;
                    continue;
                }

                if (translation.FileId != currentFileId)
                {
                    if (currentSubFile is not null && currentFileId != -1)
                    {
                        byte[] previousData = currentSubFile.Serialize();
                        Result putSubfileDataResult = _datFileHandler.PutSubfileData(
                            handle: datFileHandle,
                            fileId: currentFileId,
                            data: previousData,
                            version: currentSubFile.Version,
                            iteration: fileSizes[currentFileId].Iteration);

                        if (putSubfileDataResult.IsFailure)
                        {
                            warnings.Add(putSubfileDataResult.Error.Message);
                        }
                    }

                    (int size, int _) = fileSizes[translation.FileId];
                    Result<SubFile> loadResult = _datFileHandler.LoadSubFile(
                        handle: datFileHandle,
                        fileId: translation.FileId,
                        size: size,
                        loadVersion: true);

                    if (loadResult.IsFailure)
                    {
                        warnings.Add(loadResult.Error.Message);
                        currentSubFile = null;
                        currentFileId = -1;
                        skippedCount++;
                        continue;
                    }

                    currentSubFile = loadResult.Value;
                    currentFileId = translation.FileId;
                }

                if (currentSubFile is null)
                {
                    continue;
                }

                if (currentSubFile.TryGetFragment(translation.FragmentId, out Fragment? fragment)
                    && fragment is not null)
                {
                    fragment.Pieces = [.. pieces];

                    if (translation.ArgsOrder is not null && fragment.HasArguments)
                    {
                        if (!fragment.TryReorderArgRefs(translation.ArgsOrder))
                        {
                            warnings.Add(
                                $"ArgRefs reorder failed for fragment {translation.GossipId} in file {translation.FileId}");
                        }
                    }

                    appliedCount++;

                    if (appliedCount % ProgressReportInterval == 0)
                    {
                        progress?.Report(new OperationProgress(appliedCount, translations.Count));
                    }
                }
                else
                {
                    warnings.Add($"Fragment {translation.GossipId} not found in file {translation.FileId}");
                    skippedCount++;
                }
            }

            if (currentSubFile is not null && currentFileId != -1)
            {
                byte[] lastData = currentSubFile.Serialize();
                Result putSubfileDataResult = _datFileHandler.PutSubfileData(
                    handle: datFileHandle,
                    fileId: currentFileId,
                    data: lastData,
                    version: currentSubFile.Version,
                    iteration: fileSizes[currentFileId].Iteration);

                if (putSubfileDataResult.IsFailure)
                {
                    warnings.Add(putSubfileDataResult.Error.Message);
                }
            }

            PatchSummaryResponse summary = new(
                translations.Count,
                appliedCount,
                skippedCount,
                warnings);

            return Result.Success(summary);
        }
        finally
        {
            _datFileHandler.Flush(datFileHandle);
            _datFileHandler.Close(datFileHandle);
        }
    }
}
