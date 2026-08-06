using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Application.Features.Patching;
using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;
using LotroKoniecDev.Primitives.Constants;
using LotroKoniecDev.Primitives.Enums;
using LotroKoniecDev.Tests.Unit.Shared;

namespace LotroKoniecDev.Tests.Unit.Tests.Features;

public sealed class PatchingServiceTests
{
    private readonly IDatFileHandler _datFileHandler;
    private readonly ITranslationParser _translationParser;
    private readonly PatchingService _sut;

    private const int DatHandle = 42;
    private const int TextFileId = 0x25000001;
    private const int TextFileId2 = 0x25000002;
    private const ulong FragmentId1 = 1001;
    private const ulong FragmentId2 = 1002;

    public PatchingServiceTests()
    {
        _datFileHandler = Substitute.For<IDatFileHandler>();
        _translationParser = Substitute.For<ITranslationParser>();
        _sut = new PatchingService(_datFileHandler, _translationParser);
    }

    private void SetupAllPassingDefaults()
    {
        _datFileHandler.Open(Arg.Any<string>(), DatFileAccess.ReadWrite).Returns(Result.Success(DatHandle));
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
        SetupParsedFile(translations, []);
    }

    private void SetupParsedFile(IReadOnlyList<Translation> translations, IReadOnlyList<string> warnings)
    {
        _translationParser.ParseFile(Arg.Any<string>())
            .Returns(Result.Success(new TranslationParseResult(translations, warnings, warnings.Count)));
    }

    private static Translation CreateTranslation(
        int fileId = TextFileId,
        ulong gossipId = FragmentId1,
        string content = "Przetlumaczony tekst",
        int[]? argsOrder = null,
        bool isApproved = true) =>
        new()
        {
            FileId = fileId,
            GossipId = gossipId,
            Content = content,
            ArgsOrder = argsOrder,
            ArgsId = null,
            IsApproved = isApproved
        };

    [Fact]
    public void ApplyTranslations_HappyPath_ShouldReturnPatchSummary()
    {
        // Arrange
        SetupAllPassingDefaults();
        SetupTranslations(CreateTranslation());

        // Act
        Result<PatchSummaryResponse> result = _sut.ApplyTranslations("/translations/polish.txt", "/game/client_local_English.dat");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalTranslations.ShouldBe(1);
        result.Value.AppliedTranslations.ShouldBe(1);
        result.Value.SkippedTranslations.ShouldBe(0);
    }

    [Fact]
    public void ApplyTranslations_TranslationParseFails_ShouldReturnFailure()
    {
        // Arrange
        SetupAllPassingDefaults();
        Error parseError = new("Translation.ParseError", "Bad format", ErrorType.Validation);
        _translationParser.ParseFile(Arg.Any<string>())
            .Returns(Result.Failure<TranslationParseResult>(parseError));

        // Act
        Result<PatchSummaryResponse> result = _sut.ApplyTranslations("/translations/polish.txt", "/game/client_local_English.dat");

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Translation.ParseError");
    }

    [Fact]
    public void ApplyTranslations_WhenTheParserRejectedALine_ShouldSurfaceItsWarningInTheSummary()
    {
        // Arrange — a rejected line used to vanish inside the parser (ADR-0042). The summary is the
        // only channel the CLI prints, so a warning that does not reach it is still swallowed.
        SetupAllPassingDefaults();
        SetupParsedFile([CreateTranslation()], ["Line 7: the args_order column '1-x' is malformed."]);

        // Act
        Result<PatchSummaryResponse> result = _sut.ApplyTranslations("/translations/polish.txt", "/game/client_local_English.dat");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Warnings.ShouldContain("Line 7: the args_order column '1-x' is malformed.");
    }

    [Fact]
    public void ApplyTranslations_WhenEveryLineWasRejected_ShouldSayWhyInsteadOfJustNoTranslations()
    {
        // Arrange — the failure path drops the warning list (the CLI prints it only on success), so
        // without the reason in the Error itself a wholly corrupt polish.txt would report a bare
        // "No translations to apply" — the exact silence ADR-0042 exists to remove.
        SetupAllPassingDefaults();
        SetupParsedFile([], ["Error parsing line '1||2||c||1-x||NULL||1': the args_order column is malformed."]);

        // Act
        Result<PatchSummaryResponse> result = _sut.ApplyTranslations("/translations/polish.txt", "/game/client_local_English.dat");

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Translation.NoTranslations");
        result.Error.Message.ShouldContain("args_order");
    }

    [Fact]
    public void ApplyTranslations_WhenTheFileHeldNoRowsAtAll_ShouldReportPlainNoTranslations()
    {
        // Arrange — an empty or comments-only file is not a corruption, so it must not be dressed up
        // as one; nothing was rejected and there is no reason to give.
        SetupAllPassingDefaults();
        SetupParsedFile([], []);

        // Act
        Result<PatchSummaryResponse> result = _sut.ApplyTranslations("/translations/polish.txt", "/game/client_local_English.dat");

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Translation.NoTranslations");
        result.Error.Message.ShouldBe("No translations to apply.");
    }

    [Fact]
    public void ApplyTranslations_NoTranslations_ShouldReturnFailure()
    {
        // Arrange
        SetupAllPassingDefaults();
        SetupTranslations(); // empty array

        // Act
        Result<PatchSummaryResponse> result = _sut.ApplyTranslations("/translations/polish.txt", "/game/client_local_English.dat");

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Translation.NoTranslations");
    }

    [Fact]
    public void ApplyTranslations_DatFileOpenFails_ShouldReturnFailure()
    {
        // Arrange
        SetupAllPassingDefaults();
        SetupTranslations(CreateTranslation());

        Error openError = new("DatFile.CannotOpen", "Locked", ErrorType.IoError);
        _datFileHandler.Open(Arg.Any<string>(), DatFileAccess.ReadWrite).Returns(Result.Failure<int>(openError));

        // Act
        Result<PatchSummaryResponse> result = _sut.ApplyTranslations("/translations/polish.txt", "/game/client_local_English.dat");

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("DatFile.CannotOpen");
    }

    [Fact]
    public void ApplyTranslations_FileNotInDat_ShouldSkipAndWarn()
    {
        // Arrange
        SetupAllPassingDefaults();

        const int missingFileId = 0x25999999;
        Translation translation = CreateTranslation(fileId: missingFileId);
        SetupTranslations(translation);

        // Act
        Result<PatchSummaryResponse> result = _sut.ApplyTranslations("/translations/polish.txt", "/game/client_local_English.dat");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.SkippedTranslations.ShouldBe(1);
        result.Value.AppliedTranslations.ShouldBe(0);
        result.Value.Warnings.ShouldContain(w => w.Contains("not found in DAT"));
    }

    [Fact]
    public void ApplyTranslations_NonTextFile_ShouldSkipAndWarn()
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

        // Act
        Result<PatchSummaryResponse> result = _sut.ApplyTranslations("/translations/polish.txt", "/game/client_local_English.dat");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.SkippedTranslations.ShouldBe(1);
        result.Value.Warnings.ShouldContain(w => w.Contains("not a text file"));
    }

    [Fact]
    public void ApplyTranslations_PieceLongerThanTheDatAllows_ShouldSkipAndWarnWithoutWritingTheSubFile()
    {
        // Arrange — the TMS caps the text at its API, so this row can only come from a hand-edited or
        // hostile polish.txt. It used to reach VarLenEncoder.Write and throw mid-loop (#598).
        SetupAllPassingDefaults();
        SetupTranslations(CreateTranslation(content: new string('ż', DatFileConstants.MaxTextPieceLength + 1)));

        // Act
        Result<PatchSummaryResponse> result = _sut.ApplyTranslations("/translations/polish.txt", "/game/client_local_English.dat");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.AppliedTranslations.ShouldBe(0);
        result.Value.SkippedTranslations.ShouldBe(1);
        result.Value.Warnings.ShouldContain(w => w.Contains("32768 characters"));

        // The row is screened before any subfile is loaded, so nothing is committed on its account —
        // this is what keeps a bad row from leaving a half-patched DAT behind.
        _datFileHandler.DidNotReceive().PutSubfileData(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>());
    }

    [Fact]
    public void ApplyTranslations_PieceLongerThanTheDatAllows_ShouldStillApplyEveryOtherRow()
    {
        // Arrange — one poisoned row must cost exactly one row, not the whole patch run. Both rows
        // target the same fragment because the fixture subfile holds exactly one; what is under test
        // is that the loop survives the first row, not which fragment the second one lands on.
        SetupAllPassingDefaults();
        SetupTranslations(
            CreateTranslation(gossipId: FragmentId1, content: new string('ż', DatFileConstants.MaxTextPieceLength + 1)),
            CreateTranslation(gossipId: FragmentId1, content: "Zdrowy tekst"));

        // Act
        Result<PatchSummaryResponse> result = _sut.ApplyTranslations("/translations/polish.txt", "/game/client_local_English.dat");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.AppliedTranslations.ShouldBe(1);
        result.Value.SkippedTranslations.ShouldBe(1);

        // The counters alone would also be satisfied by a run that applied the healthy row in memory
        // and never committed it. The commit is invisible in the Result, so assert it here.
        _datFileHandler.Received(1).PutSubfileData(
            DatHandle, TextFileId, Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>());
    }

    [Fact]
    public void ApplyTranslations_ContentSplittingIntoPiecesThatEachFit_ShouldApply()
    {
        // Arrange — the DAT caps each PIECE, not the whole row, and the patcher cuts pieces on the
        // placeholder. Content twice the ceiling is therefore legal as long as no piece exceeds it.
        SetupAllPassingDefaults();
        string half = new('ż', DatFileConstants.MaxTextPieceLength);
        SetupTranslations(CreateTranslation(content: $"{half}{DatFileConstants.PieceSeparator}{half}"));

        // Act
        Result<PatchSummaryResponse> result = _sut.ApplyTranslations("/translations/polish.txt", "/game/client_local_English.dat");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.AppliedTranslations.ShouldBe(1);
        result.Value.SkippedTranslations.ShouldBe(0);
    }

    [Fact]
    public void ApplyTranslations_FragmentNotFound_ShouldSkipAndWarn()
    {
        // Arrange
        SetupAllPassingDefaults();

        const ulong missingFragmentId = 9999;
        Translation translation = CreateTranslation(gossipId: (int)missingFragmentId);
        SetupTranslations(translation);

        // Act
        Result<PatchSummaryResponse> result = _sut.ApplyTranslations("/translations/polish.txt", "/game/client_local_English.dat");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.SkippedTranslations.ShouldBe(1);
        result.Value.Warnings.ShouldContain(w => w.Contains("Fragment 9999 not found"));
    }

    [Fact]
    public void ApplyTranslations_SubFileLoadFails_ShouldSkipAndWarn()
    {
        // Arrange
        SetupAllPassingDefaults();
        SetupTranslations(CreateTranslation());

        Error loadError = new("SubFile.ParseError", "Corrupted", ErrorType.IoError);
        _datFileHandler.GetSubfileData(DatHandle, TextFileId, 100)
            .Returns(Result.Failure<byte[]>(loadError));

        // Act
        Result<PatchSummaryResponse> result = _sut.ApplyTranslations("/translations/polish.txt", "/game/client_local_English.dat");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.SkippedTranslations.ShouldBe(1);
    }

    [Fact]
    public void ApplyTranslations_HappyPath_ShouldFlushAndClose()
    {
        // Arrange
        SetupAllPassingDefaults();
        SetupTranslations(CreateTranslation());

        // Act
        _sut.ApplyTranslations("/translations/polish.txt", "/game/client_local_English.dat");

        // Assert
        _datFileHandler.Received(1).Flush(DatHandle);
        _datFileHandler.Received(1).Close(DatHandle);
    }

    [Fact]
    public void ApplyTranslations_PatchingSucceeds_ShouldSaveSubFile()
    {
        // Arrange
        SetupAllPassingDefaults();
        SetupTranslations(CreateTranslation());

        // Act
        _sut.ApplyTranslations("/translations/polish.txt", "/game/client_local_English.dat");

        // Assert
        _datFileHandler.Received(1).PutSubfileData(
            DatHandle, TextFileId, Arg.Any<byte[]>(), Arg.Any<int>(), 1);
    }

    [Fact]
    public void ApplyTranslations_PutSubfileDataFails_ShouldAddWarningAndContinue()
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

        Error putError = new("DatFile.WriteError", "Disk full", ErrorType.IoError);
        _datFileHandler.PutSubfileData(DatHandle, TextFileId, Arg.Any<byte[]>(), Arg.Any<int>(), 1)
            .Returns(Result.Failure(putError));
        _datFileHandler.PutSubfileData(DatHandle, TextFileId2, Arg.Any<byte[]>(), Arg.Any<int>(), 2)
            .Returns(Result.Success());

        Translation t1 = CreateTranslation(fileId: TextFileId);
        Translation t2 = CreateTranslation(fileId: TextFileId2);
        SetupTranslations(t1, t2);

        // Act
        Result<PatchSummaryResponse> result = _sut.ApplyTranslations("/translations/polish.txt", "/game/client_local_English.dat");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.AppliedTranslations.ShouldBe(2);
        result.Value.Warnings.ShouldContain(w => w.Contains("Disk full"));
    }

    [Fact]
    public void ApplyTranslations_MultipleTranslationsSameFile_ShouldSaveOnce()
    {
        // Arrange
        SetupAllPassingDefaults();

        byte[] subFileData = TestDataFactory.CreateTextSubFileData(TextFileId, FragmentId1, 2);
        _datFileHandler.GetSubfileData(DatHandle, TextFileId, 100)
            .Returns(Result.Success(subFileData));

        Translation t1 = CreateTranslation(gossipId: FragmentId1);
        Translation t2 = CreateTranslation(gossipId: FragmentId2);
        SetupTranslations(t1, t2);

        // Act
        Result<PatchSummaryResponse> result = _sut.ApplyTranslations("/translations/polish.txt", "/game/client_local_English.dat");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.AppliedTranslations.ShouldBe(2);
        _datFileHandler.Received(1).PutSubfileData(
            DatHandle, TextFileId, Arg.Any<byte[]>(), Arg.Any<int>(), 1);
    }

    [Fact]
    public void ApplyTranslations_TranslationsInDifferentFiles_ShouldSaveEach()
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

        // Act
        Result<PatchSummaryResponse> result = _sut.ApplyTranslations("/translations/polish.txt", "/game/client_local_English.dat");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.AppliedTranslations.ShouldBe(2);
        _datFileHandler.Received(1).PutSubfileData(
            DatHandle, TextFileId, Arg.Any<byte[]>(), Arg.Any<int>(), 1);
        _datFileHandler.Received(1).PutSubfileData(
            DatHandle, TextFileId2, Arg.Any<byte[]>(), Arg.Any<int>(), 2);
    }

    [Fact]
    public void ApplyTranslations_UnapprovedTranslation_ShouldSkip()
    {
        // Arrange
        SetupAllPassingDefaults();
        SetupTranslations(CreateTranslation(isApproved: false));

        // Act
        Result<PatchSummaryResponse> result = _sut.ApplyTranslations("/translations/polish.txt", "/game/client_local_English.dat");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.AppliedTranslations.ShouldBe(0);
        result.Value.SkippedTranslations.ShouldBe(1);
    }

    [Fact]
    public void ApplyTranslations_MixedApproval_ShouldOnlyApplyApproved()
    {
        // Arrange
        SetupAllPassingDefaults();

        byte[] subFileData = TestDataFactory.CreateTextSubFileData(TextFileId, FragmentId1, 2);
        _datFileHandler.GetSubfileData(DatHandle, TextFileId, 100)
            .Returns(Result.Success(subFileData));

        Translation approved = CreateTranslation(gossipId: FragmentId1, isApproved: true);
        Translation unapproved = CreateTranslation(gossipId: FragmentId2, isApproved: false);
        SetupTranslations(approved, unapproved);

        // Act
        Result<PatchSummaryResponse> result = _sut.ApplyTranslations("/translations/polish.txt", "/game/client_local_English.dat");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.AppliedTranslations.ShouldBe(1);
        result.Value.SkippedTranslations.ShouldBe(1);
    }

    [Fact]
    public void ApplyTranslations_WithArgsOrder_ShouldReorderArgRefs()
    {
        // Arrange
        byte[][] argRefs =
        [
            [0x01, 0x00, 0x00, 0x00],
            [0x02, 0x00, 0x00, 0x00]
        ];
        byte[] subFileData = TestDataFactory.CreateTextSubFileDataWithArgs(
            TextFileId, FragmentId1, ["Part1", "Part2", "Part3"], argRefs);

        _datFileHandler.Open(Arg.Any<string>(), DatFileAccess.ReadWrite).Returns(Result.Success(DatHandle));
        _datFileHandler.GetAllSubfileSizes(DatHandle).Returns(new Dictionary<int, (int, int)>
        {
            { TextFileId, (subFileData.Length, 1) }
        });
        _datFileHandler.GetSubfileData(DatHandle, TextFileId, subFileData.Length)
            .Returns(Result.Success(subFileData));
        _datFileHandler.GetSubfileVersion(DatHandle, TextFileId).Returns(1);
        _datFileHandler.PutSubfileData(DatHandle, Arg.Any<int>(), Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(Result.Success());

        // Translation with swapped arg order: [1, 0] means new[0]=old[1], new[1]=old[0]
        Translation translation = CreateTranslation(
            content: "Czesc1<--DO_NOT_TOUCH!-->Czesc2<--DO_NOT_TOUCH!-->Czesc3",
            argsOrder: [1, 0]);
        SetupTranslations(translation);

        byte[]? capturedData = null;
        _datFileHandler.PutSubfileData(DatHandle, TextFileId, Arg.Do<byte[]>(d => capturedData = d), Arg.Any<int>(), 1)
            .Returns(Result.Success());

        // Act
        Result<PatchSummaryResponse> result = _sut.ApplyTranslations("/translations/polish.txt", "/game/client_local_English.dat");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.AppliedTranslations.ShouldBe(1);

        capturedData.ShouldNotBeNull();
        SubFile verifySubFile = new();
        verifySubFile.Parse(capturedData);
        verifySubFile.TryGetFragment(FragmentId1, out Fragment? verifyFragment).ShouldBeTrue();
        verifyFragment!.ArgRefs[0].ShouldBe(new byte[] { 0x02, 0x00, 0x00, 0x00 });
        verifyFragment.ArgRefs[1].ShouldBe(new byte[] { 0x01, 0x00, 0x00, 0x00 });
    }

    [Fact]
    public void ApplyTranslations_ExecutionOrder_ShouldBeParseThenPatch()
    {
        // Arrange
        SetupAllPassingDefaults();

        List<string> callOrder = [];

        _translationParser.ParseFile(Arg.Any<string>())
            .Returns(_ =>
            {
                callOrder.Add("parse");
                return Result.Success(new TranslationParseResult([CreateTranslation()], [], 0));
            });

        _datFileHandler.Open(Arg.Any<string>(), DatFileAccess.ReadWrite)
            .Returns(_ =>
            {
                callOrder.Add("open_dat");
                return Result.Success(DatHandle);
            });

        // Act
        _sut.ApplyTranslations("/translations/polish.txt", "/game/client_local_English.dat");

        // Assert
        callOrder.ShouldBe(["parse", "open_dat"]);
    }
}
