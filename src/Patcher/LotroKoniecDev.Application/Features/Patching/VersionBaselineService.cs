using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Application.Features.Patching;

internal sealed class VersionBaselineService : IVersionBaselineService
{
    private readonly IFileHasher _fileHasher;
    private readonly IGameVersionFileStore _versionStore;

    public VersionBaselineService(
        IFileHasher fileHasher,
        IGameVersionFileStore versionStore)
    {
        _fileHasher = fileHasher;
        _versionStore = versionStore;
    }

    public Result SaveBaseline(
        DatVersionInfo datVersion,
        string? forumVersion,
        string translationFilePath,
        string versionFilePath)
    {
        Result<string> hashResult = _fileHasher.ComputeHash(translationFilePath);
        string? translationHash = hashResult.IsSuccess ? hashResult.Value : null;

        return _versionStore.SaveVersion(
            versionFilePath: versionFilePath,
            forumVersion: forumVersion,
            vnumDatFile: datVersion.VnumDatFile,
            vnumGameData: datVersion.VnumGameData,
            translationFileHash: translationHash);
    }
}
