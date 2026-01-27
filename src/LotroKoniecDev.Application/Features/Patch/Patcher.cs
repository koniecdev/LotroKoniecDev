using LotroKoniecDev.Application.Abstractions;
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

    public Patcher(IDatFileHandler datFileHandler, ITranslationParser translationParser)
    {
        _datFileHandler = datFileHandler ?? throw new ArgumentNullException(nameof(datFileHandler));
        _translationParser = translationParser ?? throw new ArgumentNullException(nameof(translationParser));
    }

    public Result<PatchSummary> ApplyTranslations(
        string translationsPath,
        string datFilePath,
        Action<int, int>? progress = null)
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
            return ProcessPatching(handle, translations, progress);
        }
        finally
        {
            _datFileHandler.Flush(handle);
            _datFileHandler.Close(handle);
        }
    }

    private Result<PatchSummary> ProcessPatching(
        int handle,
        IReadOnlyList<Translation> translations,
        Action<int, int>? progress)
    {
        Dictionary<int, (int Size, int Iteration)> fileSizes = _datFileHandler.GetAllSubfileSizes(handle);

        var warnings = new List<string>();
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
                Result<SubFile> loadResult = LoadSubFile(handle, translation.FileId, fileSizes[translation.FileId]);

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
                        progress?.Invoke(appliedCount, translations.Count);
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

    private Result<SubFile> LoadSubFile(
        int handle,
        int fileId,
        (int Size, int Iteration) fileInfo)
    {
        Result<byte[]> dataResult = _datFileHandler.GetSubfileData(handle, fileId, fileInfo.Size);

        if (dataResult.IsFailure)
        {
            return Result.Failure<SubFile>(dataResult.Error);
        }

        try
        {
            var subFile = new SubFile
            {
                Version = _datFileHandler.GetSubfileVersion(handle, fileId)
            };

            subFile.Parse(dataResult.Value);
            return Result.Success(subFile);
        }
        catch (Exception ex)
        {
            return Result.Failure<SubFile>(
                DomainErrors.SubFile.ParseError(fileId, ex.Message));
        }
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
