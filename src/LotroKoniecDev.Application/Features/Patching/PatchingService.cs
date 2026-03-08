using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Application.Extensions;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Models;

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
        Result<IReadOnlyList<Translation>> translationParseResult =
            _translationParser.ParseFile(translationsPath);

        if (translationParseResult.IsFailure)
        {
            return Result.Failure<PatchSummaryResponse>(translationParseResult.Error);
        }

        IReadOnlyList<Translation> translations = translationParseResult.Value;

        if (translations.Count == 0)
        {
            return Result.Failure<PatchSummaryResponse>(DomainErrors.Translation.NoTranslations);
        }

        Result<int> datFileOpenResult = _datFileHandler.Open(datFilePath);

        if (datFileOpenResult.IsFailure)
        {
            return Result.Failure<PatchSummaryResponse>(datFileOpenResult.Error);
        }

        int datFileHandle = datFileOpenResult.Value;

        try
        {
            Dictionary<int, (int Size, int Iteration)> fileSizes = _datFileHandler.GetAllSubfileSizes(datFileHandle);

            List<string> warnings = [];
            int appliedCount = 0;
            int skippedCount = 0;

            int currentFileId = -1;
            SubFile? currentSubFile = null;

            foreach (Translation translation in translations)
            {
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
                    fragment.Pieces = translation.GetPieces().ToList();
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
