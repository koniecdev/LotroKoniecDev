using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Features.Patching;
using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;
using LotroKoniecDev.Primitives.Enums;

namespace LotroKoniecDev.Tests.Unit.Tests.Features;

public sealed class VersionBaselineServiceTests
{
    private const string TranslationFilePath = @"C:\translations\polish.txt";
    private const string VersionFilePath = @"C:\data\version.txt";
    private const string TranslationHash = "abc123def456";
    private static readonly DatVersionInfo DatVersion = new(100, 200);

    private readonly IFileHasher _fileHasher;
    private readonly IGameVersionFileStore _versionStore;
    private readonly VersionBaselineService _sut;

    public VersionBaselineServiceTests()
    {
        _fileHasher = Substitute.For<IFileHasher>();
        _versionStore = Substitute.For<IGameVersionFileStore>();
        _sut = new VersionBaselineService(_fileHasher, _versionStore);
    }

    [Fact]
    public void SaveBaseline_HashSucceeds_ShouldSaveWithHash()
    {
        _fileHasher.ComputeHash(TranslationFilePath).Returns(Result.Success(TranslationHash));
        _versionStore.SaveVersion(VersionFilePath, "40.2", DatVersion.VnumDatFile, DatVersion.VnumGameData, TranslationHash)
            .Returns(Result.Success());

        Result result = _sut.SaveBaseline(DatVersion, "40.2", TranslationFilePath, VersionFilePath);

        result.IsSuccess.ShouldBeTrue();
        _versionStore.Received(1).SaveVersion(
            VersionFilePath, "40.2", DatVersion.VnumDatFile, DatVersion.VnumGameData, TranslationHash);
    }

    [Fact]
    public void SaveBaseline_HashFails_ShouldSaveWithNullHash()
    {
        Error hashError = new("FileHasher.Failed", "File not found", ErrorType.IoError);
        _fileHasher.ComputeHash(TranslationFilePath).Returns(Result.Failure<string>(hashError));
        _versionStore.SaveVersion(VersionFilePath, "40.2", DatVersion.VnumDatFile, DatVersion.VnumGameData, null)
            .Returns(Result.Success());

        Result result = _sut.SaveBaseline(DatVersion, "40.2", TranslationFilePath, VersionFilePath);

        result.IsSuccess.ShouldBeTrue();
        _versionStore.Received(1).SaveVersion(
            VersionFilePath, "40.2", DatVersion.VnumDatFile, DatVersion.VnumGameData, null);
    }

    [Fact]
    public void SaveBaseline_StoreSaveFails_ShouldReturnFailure()
    {
        _fileHasher.ComputeHash(TranslationFilePath).Returns(Result.Success(TranslationHash));
        Error saveError = new("VersionStore.WriteFailed", "Access denied", ErrorType.IoError);
        _versionStore.SaveVersion(VersionFilePath, "40.2", DatVersion.VnumDatFile, DatVersion.VnumGameData, TranslationHash)
            .Returns(Result.Failure(saveError));

        Result result = _sut.SaveBaseline(DatVersion, "40.2", TranslationFilePath, VersionFilePath);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("VersionStore.WriteFailed");
    }

    [Fact]
    public void SaveBaseline_NullForumVersion_ShouldPassNullToStore()
    {
        _fileHasher.ComputeHash(TranslationFilePath).Returns(Result.Success(TranslationHash));
        _versionStore.SaveVersion(VersionFilePath, null, DatVersion.VnumDatFile, DatVersion.VnumGameData, TranslationHash)
            .Returns(Result.Success());

        Result result = _sut.SaveBaseline(DatVersion, null, TranslationFilePath, VersionFilePath);

        result.IsSuccess.ShouldBeTrue();
        _versionStore.Received(1).SaveVersion(
            VersionFilePath, null, DatVersion.VnumDatFile, DatVersion.VnumGameData, TranslationHash);
    }
}
