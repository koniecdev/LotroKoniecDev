using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Application.Features.Patching;

internal sealed class VersionBaselineService : IVersionBaselineService
{
    private readonly IDatVersionReader _datVersionReader;
    private readonly IGameUpdateChecker _updateChecker;
    private readonly IFileHasher _fileHasher;
    private readonly IGameVersionFileStore _versionStore;

    public VersionBaselineService(
        IDatVersionReader datVersionReader,
        IGameUpdateChecker updateChecker,
        IFileHasher fileHasher,
        IGameVersionFileStore versionStore)
    {
        _datVersionReader = datVersionReader;
        _updateChecker = updateChecker;
        _fileHasher = fileHasher;
        _versionStore = versionStore;
    }

    public async Task<Result> SaveBaselineAsync(
        string datFilePath, string translationFilePath, string versionFilePath)
    {
        Result<DatVersionInfo> vnumResult = _datVersionReader.ReadVersion(datFilePath);
        if (vnumResult.IsFailure)
        {
            return Result.Failure(vnumResult.Error);
        }

        Result<GameUpdateCheckSummary> checkResult =
            await _updateChecker.CheckForUpdateAsync(versionFilePath);

        string? forumVersion = checkResult.IsSuccess ? checkResult.Value.ForumVersion : null;

        Result<string> hashResult = _fileHasher.ComputeHash(translationFilePath);
        string? translationHash = hashResult.IsSuccess ? hashResult.Value : null;

        return _versionStore.SaveVersion(
            versionFilePath: versionFilePath,
            forumVersion: forumVersion,
            vnumDatFile: vnumResult.Value.VnumDatFile,
            vnumGameData: vnumResult.Value.VnumGameData,
            translationFileHash: translationHash);
    }
}
