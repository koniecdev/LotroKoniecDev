using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Extensions;
using LotroKoniecDev.Application.Progress;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Application.Features.Patch;

/// <summary>
/// Applies translations to LOTRO DAT files.
/// </summary>
public sealed class Patcher : IPatcher
{
    private const int ProgressReportInterval = 100;

    private readonly IDatFileHandler _datFileHandler;
    private readonly ITranslationParser _translationParser;
    private readonly IProgress<OperationProgress> _progress;

    public Patcher(
        IDatFileHandler datFileHandler,
        ITranslationParser translationParser,
        IProgress<OperationProgress> progress)
    {
        _datFileHandler = datFileHandler ?? throw new ArgumentNullException(nameof(datFileHandler));
        _translationParser = translationParser ?? throw new ArgumentNullException(nameof(translationParser));
        _progress = progress ?? throw new ArgumentNullException(nameof(progress));
    }

    public Result<PatchSummary> ApplyTranslations(
        string translationsPath,
        string datFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(translationsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(datFilePath);

        // Parse translations
        Result<IReadOnlyList<Translation>> parseResult = _translationParser.ParseFile(translationsPath);

        if (parseResult.IsFailure)
        {
            return Result.Failure<PatchSummary>(parseResult.Error);
        }

        IReadOnlyList<Translation> translations = parseResult.Value;

        if (translations.Count == 0)
        {
            return Result.Failure<PatchSummary>(DomainErrors.Translation.NoTranslations);
        }

        // Open DAT file
        Result<int> openResult = _datFileHandler.Open(datFilePath);

        if (openResult.IsFailure)
        {
            return Result.Failure<PatchSummary>(openResult.Error);
        }

        int handle = openResult.Value;

        try
        {
            return ProcessPatching(handle, translations);
        }
        finally
        {
            _datFileHandler.Flush(handle);
            _datFileHandler.Close(handle);
        }
    }

    private Result<PatchSummary> ProcessPatching(
        int handle,
        IReadOnlyList<Translation> translations)
    {
        Dictionary<int, (int Size, int Iteration)> fileSizes = _datFileHandler.GetAllSubfileSizes(handle);

        List<string> warnings = new List<string>();
        int appliedCount = 0;
        int skippedCount = 0;

        int currentFileId = -1;
        SubFile? currentSubFile = null;

        foreach (Translation translation in translations)
        {
            // Validate file exists and is a text file
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

            // Load subfile if needed (optimization: batch by file ID)
            if (translation.FileId != currentFileId)
            {
                // Save previous subfile if modified
                if (currentSubFile is not null && currentFileId != -1)
                {
                    SaveSubFile(handle, currentFileId, currentSubFile, fileSizes[currentFileId]);
                }

                // Load new subfile
                (int size, int _) = fileSizes[translation.FileId];
                Result<SubFile> loadResult = _datFileHandler.LoadSubFile(handle, translation.FileId, size, loadVersion: true);

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

            // Apply translation
            if (currentSubFile is not null)
            {
                if (currentSubFile.TryGetFragment(translation.FragmentId, out Fragment? fragment) && fragment is not null)
                {
                    fragment.Pieces = translation.GetPieces().ToList();
                    appliedCount++;

                    if (appliedCount % ProgressReportInterval == 0)
                    {
                        _progress.Report(new OperationProgress
                        {
                            OperationName = "Patch",
                            Current = appliedCount,
                            Total = translations.Count
                        });
                    }
                }
                else
                {
                    warnings.Add($"Fragment {translation.GossipId} not found in file {translation.FileId}");
                    skippedCount++;
                }
            }
        }

        // Save last subfile
        if (currentSubFile is not null && currentFileId != -1)
        {
            SaveSubFile(handle, currentFileId, currentSubFile, fileSizes[currentFileId]);
        }

        return Result.Success(new PatchSummary(
            translations.Count,
            appliedCount,
            skippedCount,
            warnings));
    }

    private void SaveSubFile(
        int handle,
        int fileId,
        SubFile subFile,
        (int Size, int Iteration) fileInfo)
    {
        byte[] data = subFile.Serialize();
        _datFileHandler.PutSubfileData(handle, fileId, data, subFile.Version, fileInfo.Iteration);
    }
}
