using LotroKoniecDev.Application;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Application.Features.Patch;
using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;
using LotroKoniecDev.Primitives.Enums;
using LotroKoniecDev.Tests.Unit.Shared;

namespace LotroKoniecDev.Tests.Unit.Tests.Features;

public sealed class ApplyPatchCommandHandlerTests
{
    private readonly IDatFileHandler _datFileHandler;
    private readonly ITranslationParser _translationParser;
    private readonly ApplyPatchCommandHandler _sut;

    private const int DatHandle = 42;
    private const int TextFileId = 0x25000001;
    private const int TextFileId2 = 0x25000002;
    private const ulong FragmentId1 = 1001;
    private const ulong FragmentId2 = 1002;

    public ApplyPatchCommandHandlerTests()
    {
        _datFileHandler = Substitute.For<IDatFileHandler>();
        _translationParser = Substitute.For<ITranslationParser>();
        IProgress<OperationProgress> progress = Substitute.For<IProgress<OperationProgress>>();

        _sut = new ApplyPatchCommandHandler(
            _datFileHandler,
            _translationParser,
            progress);
    }

    private static ApplyPatchCommand CreateCommand(
        string translationsPath = "/translations/polish.txt",
        string datFilePath = "/game/client_local_English.dat")
    {
        return new ApplyPatchCommand(translationsPath, datFilePath);
    }

    private void SetupAllPassingDefaults()
    {
        _datFileHandler.Open(Arg.Any<string>()).Returns(Result.Success(DatHandle));
        _datFileHandler.GetAllSubfileSizes(DatHandle).Returns(new Dictionary<int, (int, int)>
        {
            { TextFileId, (100, 1) }
        });
        _datFileHandler.GetSubfileData(DatHandle, TextFileId, 100)
            .Returns(Result.Success(TestDataFactory.CreateTextSubFileData(TextFileId, FragmentId1, 1)));
        _datFileHandler.GetSubfileVersion(DatHandle, TextFileId).Returns(1);
        _datFileHandler.PutSubfileData(DatHandle, Arg.Any<int>(), Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(Result.Success());
    }

    private void SetupTranslations(params Translation[] translations)
    {
        _translationParser.ParseFile(Arg.Any<string>())
            .Returns(Result.Success<IReadOnlyList<Translation>>(translations.ToList()));
    }

    private static Translation CreateTranslation(
        int fileId = TextFileId,
        int gossipId = (int)FragmentId1,
        string content = "Przetlumaczony tekst") =>
        new()
        {
            FileId = fileId,
            GossipId = gossipId,
            Content = content,
            ArgsOrder = null,
            ArgsId = null
        };

    [Fact]
    public async Task Handle_HappyPath_ShouldReturnPatchSummary()
    {
        // Arrange
        SetupAllPassingDefaults();

        Translation translation = CreateTranslation();
        SetupTranslations(translation);

        ApplyPatchCommand command = CreateCommand();

        // Act
        Result<PatchSummaryResponse> result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalTranslations.ShouldBe(1);
        result.Value.AppliedTranslations.ShouldBe(1);
        result.Value.SkippedTranslations.ShouldBe(0);
    }
    
    [Fact]
    public async Task Handle_TranslationParseFails_ShouldReturnFailure()
    {
        // Arrange
        SetupAllPassingDefaults();
        Error parseError = new("Translation.ParseError", "Bad format", ErrorType.Validation);
        _translationParser.ParseFile(Arg.Any<string>())
            .Returns(Result.Failure<IReadOnlyList<Translation>>(parseError));

        ApplyPatchCommand command = CreateCommand();

        // Act
        Result<PatchSummaryResponse> result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Translation.ParseError");
    }

    [Fact]
    public async Task Handle_NoTranslations_ShouldReturnFailure()
    {
        // Arrange
        SetupAllPassingDefaults();
        SetupTranslations(); // empty array

        ApplyPatchCommand command = CreateCommand();

        // Act
        Result<PatchSummaryResponse> result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Translation.NoTranslations");
    }

    [Fact]
    public async Task Handle_DatFileOpenFails_ShouldReturnFailure()
    {
        // Arrange
        SetupAllPassingDefaults();
        SetupTranslations(CreateTranslation());

        Error openError = new("DatFile.CannotOpen", "Locked", ErrorType.IoError);
        _datFileHandler.Open(Arg.Any<string>()).Returns(Result.Failure<int>(openError));

        ApplyPatchCommand command = CreateCommand();

        // Act
        Result<PatchSummaryResponse> result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("DatFile.CannotOpen");
    }

    [Fact]
    public async Task Handle_FileNotInDat_ShouldSkipAndWarn()
    {
        // Arrange
        SetupAllPassingDefaults();

        const int missingFileId = 0x25999999;
        Translation translation = CreateTranslation(fileId: missingFileId);
        SetupTranslations(translation);

        ApplyPatchCommand command = CreateCommand();

        // Act
        Result<PatchSummaryResponse> result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.SkippedTranslations.ShouldBe(1);
        result.Value.AppliedTranslations.ShouldBe(0);
        result.Value.Warnings.ShouldContain(w => w.Contains("not found in DAT"));
    }

    [Fact]
    public async Task Handle_NonTextFile_ShouldSkipAndWarn()
    {
        // Arrange
        SetupAllPassingDefaults();

        const int nonTextFileId = 0x10000001;
        Translation translation = CreateTranslation(fileId: nonTextFileId);
        SetupTranslations(translation);

        _datFileHandler.GetAllSubfileSizes(DatHandle).Returns(new Dictionary<int, (int, int)>
        {
            { nonTextFileId, (100, 1) }
        });

        ApplyPatchCommand command = CreateCommand();

        // Act
        Result<PatchSummaryResponse> result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.SkippedTranslations.ShouldBe(1);
        result.Value.Warnings.ShouldContain(w => w.Contains("not a text file"));
    }

    [Fact]
    public async Task Handle_FragmentNotFound_ShouldSkipAndWarn()
    {
        // Arrange
        SetupAllPassingDefaults();

        const ulong missingFragmentId = 9999;
        Translation translation = CreateTranslation(gossipId: (int)missingFragmentId);
        SetupTranslations(translation);

        ApplyPatchCommand command = CreateCommand();

        // Act
        Result<PatchSummaryResponse> result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.SkippedTranslations.ShouldBe(1);
        result.Value.Warnings.ShouldContain(w => w.Contains("Fragment 9999 not found"));
    }

    [Fact]
    public async Task Handle_SubFileLoadFails_ShouldSkipAndWarn()
    {
        // Arrange
        SetupAllPassingDefaults();
        SetupTranslations(CreateTranslation());

        Error loadError = new("SubFile.ParseError", "Corrupted", ErrorType.IoError);
        _datFileHandler.GetSubfileData(DatHandle, TextFileId, 100)
            .Returns(Result.Failure<byte[]>(loadError));

        ApplyPatchCommand command = CreateCommand();

        // Act
        Result<PatchSummaryResponse> result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.SkippedTranslations.ShouldBe(1);
    }

    [Fact]
    public async Task Handle_HappyPath_ShouldFlushAndClose()
    {
        // Arrange
        SetupAllPassingDefaults();
        SetupTranslations(CreateTranslation());

        ApplyPatchCommand command = CreateCommand();

        // Act
        await _sut.Handle(command, CancellationToken.None);

        // Assert
        _datFileHandler.Received(1).Flush(DatHandle);
        _datFileHandler.Received(1).Close(DatHandle);
    }

    [Fact]
    public async Task Handle_PatchingSucceeds_ShouldSaveSubFile()
    {
        // Arrange
        SetupAllPassingDefaults();
        SetupTranslations(CreateTranslation());

        ApplyPatchCommand command = CreateCommand();

        // Act
        await _sut.Handle(command, CancellationToken.None);

        // Assert
        _datFileHandler.Received(1).PutSubfileData(
            DatHandle, TextFileId, Arg.Any<byte[]>(), Arg.Any<int>(), 1);
    }

    [Fact]
    public async Task Handle_MultipleTranslationsSameFile_ShouldSaveOnce()
    {
        // Arrange
        SetupAllPassingDefaults();

        byte[] subFileData = TestDataFactory.CreateTextSubFileData(TextFileId, FragmentId1, 2);
        _datFileHandler.GetSubfileData(DatHandle, TextFileId, 100)
            .Returns(Result.Success(subFileData));

        Translation t1 = CreateTranslation(gossipId: (int)FragmentId1);
        Translation t2 = CreateTranslation(gossipId: (int)FragmentId2);
        SetupTranslations(t1, t2);

        ApplyPatchCommand command = CreateCommand();

        // Act
        Result<PatchSummaryResponse> result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.AppliedTranslations.ShouldBe(2);
        _datFileHandler.Received(1).PutSubfileData(
            DatHandle, TextFileId, Arg.Any<byte[]>(), Arg.Any<int>(), 1);
    }

    [Fact]
    public async Task Handle_TranslationsInDifferentFiles_ShouldSaveEach()
    {
        // Arrange
        SetupAllPassingDefaults();

        _datFileHandler.GetAllSubfileSizes(DatHandle).Returns(new Dictionary<int, (int, int)>
        {
            { TextFileId, (100, 1) },
            { TextFileId2, (100, 2) }
        });
        _datFileHandler.GetSubfileData(DatHandle, TextFileId, 100)
            .Returns(Result.Success(TestDataFactory.CreateTextSubFileData(TextFileId, FragmentId1, 1)));
        _datFileHandler.GetSubfileData(DatHandle, TextFileId2, 100)
            .Returns(Result.Success(TestDataFactory.CreateTextSubFileData(TextFileId2, FragmentId1, 1)));
        _datFileHandler.GetSubfileVersion(DatHandle, TextFileId).Returns(1);
        _datFileHandler.GetSubfileVersion(DatHandle, TextFileId2).Returns(2);

        Translation t1 = CreateTranslation(fileId: TextFileId);
        Translation t2 = CreateTranslation(fileId: TextFileId2);
        SetupTranslations(t1, t2);

        ApplyPatchCommand command = CreateCommand();

        // Act
        Result<PatchSummaryResponse> result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.AppliedTranslations.ShouldBe(2);
        _datFileHandler.Received(1).PutSubfileData(
            DatHandle, TextFileId, Arg.Any<byte[]>(), Arg.Any<int>(), 1);
        _datFileHandler.Received(1).PutSubfileData(
            DatHandle, TextFileId2, Arg.Any<byte[]>(), Arg.Any<int>(), 2);
    }
    
    [Fact]
    public async Task Handle_NullCommand_ShouldThrow()
    {
        // Act & Assert
        await Should.ThrowAsync<ArgumentNullException>(
            () => _sut.Handle(null!, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Handle_ExecutionOrder_ShouldBeParseThenPatch()
    {
        // Arrange
        SetupAllPassingDefaults();
        ApplyPatchCommand command = CreateCommand();

        List<string> callOrder = [];

        _translationParser.ParseFile(Arg.Any<string>())
            .Returns(_ =>
            {
                callOrder.Add("parse");
                return Result.Success<IReadOnlyList<Translation>>(new List<Translation> { CreateTranslation() });
            });

        _datFileHandler.Open(Arg.Any<string>())
            .Returns(_ =>
            {
                callOrder.Add("open_dat");
                return Result.Success(DatHandle);
            });

        // Act
        await _sut.Handle(command, CancellationToken.None);

        // Assert
        callOrder.ShouldBe(["parse", "open_dat"]);
    }
}
