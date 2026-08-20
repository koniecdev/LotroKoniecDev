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
    private readonly ITranslationLedger _translationLedger;
    private readonly PatchingService _sut;

    private const int DatHandle = 42;
    private const int TextFileId = 0x25000001;
    private const int TextFileId2 = 0x25000002;
    private const ulong FragmentId1 = 1001;
    private const ulong FragmentId2 = 1002;

    /// <summary>The text every fixture fragment holds, so the guard's "pristine English" clause is the default.</summary>
    private const string FixtureFragmentText = "Test";

    /// <summary>
    /// The digest of the fixture fragment's own export form (ADR-0047 §3, clause (a)). A row
    /// carrying it is one made for the English the DAT actually holds — the ordinary case, so it is
    /// what <see cref="CreateTranslation"/> defaults to.
    /// </summary>
    private static readonly string PristineSourceDigest = SourceDigest.ForExportForm(FixtureFragmentText, 0);

    public PatchingServiceTests()
    {
        _datFileHandler = Substitute.For<IDatFileHandler>();
        _translationParser = Substitute.For<ITranslationParser>();
        _translationLedger = Substitute.For<ITranslationLedger>();
        _translationLedger.Read(Arg.Any<string>()).Returns(new Dictionary<LedgerKey, string>());
        _translationLedger
            .Save(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<LedgerKey, string>>())
            .Returns(Result.Success());

        _sut = new PatchingService(_datFileHandler, _translationParser, _translationLedger);
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
        bool isApproved = true,
        string? sourceDigest = null) =>
        new()
        {
            FileId = fileId,
            GossipId = gossipId,
            Content = content,
            ArgsOrder = argsOrder,
            ArgsId = null,
            IsApproved = isApproved,
            // Since ADR-0047 a row is written only when it says which English it was made for, so
            // "a row that patches" defaults to the digest of what the fixture fragment holds.
            SourceDigest = sourceDigest ?? PristineSourceDigest
        };

    /// <summary>A row off a six-column translation file — hand-made, or an artifact predating ADR-0047.</summary>
    private static Translation CreateSixColumnTranslation() =>
        new()
        {
            FileId = TextFileId,
            GossipId = FragmentId1,
            Content = "Przetlumaczony tekst",
            IsApproved = true
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
        // Arrange: a rejected line used to vanish inside the parser (ADR-0042). The summary is the
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
        // Arrange: the failure path drops the warning list (the CLI prints it only on success), so
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
        // Arrange: an empty or comments-only file is not a corruption, so it must not be dressed up
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
        // Arrange: the TMS caps the text at its API, so this row can only come from a hand-edited or
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
        // Arrange: one poisoned row must cost exactly one row, not the whole patch run. Both rows
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
        // Arrange: the DAT caps each PIECE, not the whole row, and the patcher cuts pieces on the
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
            argsOrder: [1, 0],
            // This fixture's fragment carries three pieces and two argument references, so its
            // export form differs from the default one-piece fixture's.
            sourceDigest: SourceDigest.ForExportForm("Part1<--DO_NOT_TOUCH!-->Part2<--DO_NOT_TOUCH!-->Part3", 2));
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

    [Fact]
    public void ApplyTranslations_RowMadeForTheEnglishTheDatHolds_ShouldWriteIt()
    {
        // Arrange: clause (a) of ADR-0047 §3: pristine, or collaterally reverted by the launcher.
        // This is the case Tier 0/1 repair exists for, so it must stay wide open.
        SetupAllPassingDefaults();
        SetupTranslations(CreateTranslation(sourceDigest: PristineSourceDigest));

        // Act
        Result<PatchSummaryResponse> result = _sut.ApplyTranslations("/translations/polish.txt", "/game/client_local_English.dat");

        // Assert
        result.Value.AppliedTranslations.ShouldBe(1);
        result.Value.SourceMovedTranslations.ShouldBe(0);
    }

    [Fact]
    public void ApplyTranslations_EnglishChangedUnderTheRow_ShouldSkipItAndReportSourceMoved()
    {
        // Arrange: THE invariant (ADR-0047): SSG reworded the row in version N+1 and the newest
        // approved translation is still for N, so the player sees English. Whatever path writes.
        SetupAllPassingDefaults();
        SetupTranslations(CreateTranslation(sourceDigest: SourceDigest.ForExportForm("Some other English", 0)));

        // Act
        Result<PatchSummaryResponse> result = _sut.ApplyTranslations("/translations/polish.txt", "/game/client_local_English.dat");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.AppliedTranslations.ShouldBe(0);
        result.Value.SkippedTranslations.ShouldBe(1);
        result.Value.SourceMovedTranslations.ShouldBe(1);
        result.Value.Warnings.ShouldContain(warning => warning.Contains("source moved"));
    }

    [Fact]
    public void ApplyTranslations_EnglishChangedUnderTheRow_ShouldNotWriteTheSubFileAtAll()
    {
        // Arrange: the counters alone would also be satisfied by a run that mutated the fragment in
        // memory and merely declined to count it. Skipping has to mean "wrote nothing".
        SetupAllPassingDefaults();
        SetupTranslations(CreateTranslation(sourceDigest: SourceDigest.ForExportForm("Some other English", 0)));

        // Act
        _sut.ApplyTranslations("/translations/polish.txt", "/game/client_local_English.dat");

        // Assert
        _datFileHandler.DidNotReceive().PutSubfileData(
            Arg.Any<int>(), TextFileId, Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>());
    }

    [Fact]
    public void ApplyTranslations_FragmentHoldingWhatWeWroteBefore_ShouldAcceptANewerTranslation()
    {
        // Arrange: clause (b): the fragment holds our older Polish, which matches neither the row's
        // English digest nor the new translation. Only the ledger can admit this write, and without
        // it an updated translation could never reach a fragment we had already patched.
        SetupAllPassingDefaults();
        _datFileHandler.GetSubfileData(DatHandle, TextFileId, 100)
            .Returns(Result.Success(TestDataFactory.CreateTextSubFileData(TextFileId, "Stara wersja")));
        _translationLedger.Read(Arg.Any<string>()).Returns(new Dictionary<LedgerKey, string>
        {
            [new LedgerKey(TextFileId, 1)] = SourceDigest.ForExportForm("Stara wersja", 0)
        });
        SetupTranslations(CreateTranslation(gossipId: 1, content: "Nowa wersja"));

        // Act
        Result<PatchSummaryResponse> result = _sut.ApplyTranslations("/translations/polish.txt", "/game/client_local_English.dat");

        // Assert
        result.Value.AppliedTranslations.ShouldBe(1);
        result.Value.SourceMovedTranslations.ShouldBe(0);
    }

    [Fact]
    public void ApplyTranslations_FragmentHoldingOurOlderPolishWithNoLedger_ShouldSkipItAndReportSourceMoved()
    {
        // Arrange: the documented degradation of a lost ledger (ADR-0047 §4): under-patching, never
        // masking. The row is skipped until an update reverts its SubFile or the DAT is restored.
        SetupAllPassingDefaults();
        _datFileHandler.GetSubfileData(DatHandle, TextFileId, 100)
            .Returns(Result.Success(TestDataFactory.CreateTextSubFileData(TextFileId, "Stara wersja")));
        SetupTranslations(CreateTranslation(gossipId: 1, content: "Nowa wersja"));

        // Act
        Result<PatchSummaryResponse> result = _sut.ApplyTranslations("/translations/polish.txt", "/game/client_local_English.dat");

        // Assert
        result.Value.AppliedTranslations.ShouldBe(0);
        result.Value.SourceMovedTranslations.ShouldBe(1);
    }

    [Fact]
    public void ApplyTranslations_FragmentAlreadyHoldingExactlyThisTranslation_ShouldStillApplyAndSeedTheLedger()
    {
        // Arrange: clause (c): a re-run over a DAT patched before the ledger existed. Nothing but
        // our own patch puts that text there, so the write is a safe no-op and it bootstraps the
        // ledger entry that later clause-(b) writes depend on.
        SetupAllPassingDefaults();
        _datFileHandler.GetSubfileData(DatHandle, TextFileId, 100)
            .Returns(Result.Success(TestDataFactory.CreateTextSubFileData(TextFileId, "Juz przetlumaczone")));
        SetupTranslations(CreateTranslation(
            gossipId: 1,
            content: "Juz przetlumaczone",
            sourceDigest: SourceDigest.ForExportForm("Angielski, ktorego juz tam nie ma", 0)));

        // Act
        Result<PatchSummaryResponse> result = _sut.ApplyTranslations("/translations/polish.txt", "/game/client_local_English.dat");

        // Assert
        result.Value.AppliedTranslations.ShouldBe(1);
        _translationLedger.Received(1).Save(
            "/translations/polish.txt",
            Arg.Is<IReadOnlyDictionary<LedgerKey, string>>(entries =>
                entries[new LedgerKey(TextFileId, 1)] == SourceDigest.ForExportForm("Juz przetlumaczone", 0)));
    }

    [Fact]
    public void ApplyTranslations_HandMadeRowWithNullArgsOnAnArgumentBearingFragment_ShouldStillMatchItsOwnTranslation()
    {
        // Arrange: clause (c) takes the argument count from the FRAGMENT, never from the row's own
        // args columns, which a hand-made file may leave as NULL. Reading them from the row would
        // make an already-translated arg-bearing fragment fail every clause and report a false
        // "source moved" on every run.
        const string polish = "Czesc1<--DO_NOT_TOUCH!-->Czesc2";
        byte[][] argRefs = [[0x01, 0x00, 0x00, 0x00], [0x02, 0x00, 0x00, 0x00]];
        byte[] subFileData = TestDataFactory.CreateTextSubFileDataWithArgs(
            TextFileId, FragmentId1, ["Czesc1", "Czesc2"], argRefs);

        _datFileHandler.Open(Arg.Any<string>(), DatFileAccess.ReadWrite).Returns(Result.Success(DatHandle));
        _datFileHandler.GetAllSubfileSizes(DatHandle).Returns(new Dictionary<int, (int, int)>
        {
            { TextFileId, (subFileData.Length, 1) }
        });
        _datFileHandler.GetSubfileData(DatHandle, TextFileId, subFileData.Length).Returns(Result.Success(subFileData));
        _datFileHandler.GetSubfileVersion(DatHandle, TextFileId).Returns(1);
        _datFileHandler.PutSubfileData(DatHandle, Arg.Any<int>(), Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(Result.Success());

        SetupTranslations(CreateTranslation(
            content: polish,
            argsOrder: null,
            sourceDigest: SourceDigest.ForExportForm("English that has since moved", 2)));

        // Act
        Result<PatchSummaryResponse> result = _sut.ApplyTranslations("/translations/polish.txt", "/game/client_local_English.dat");

        // Assert
        result.Value.AppliedTranslations.ShouldBe(1);
        result.Value.SourceMovedTranslations.ShouldBe(0);
    }

    [Fact]
    public void ApplyTranslations_RowWithoutASourceDigest_ShouldSkipItAndSayWhy()
    {
        // Arrange: a six-column translation file: hand-made, or an artifact from before ADR-0047.
        SetupAllPassingDefaults();
        SetupTranslations(CreateSixColumnTranslation());

        // Act
        Result<PatchSummaryResponse> result = _sut.ApplyTranslations("/translations/polish.txt", "/game/client_local_English.dat");

        // Assert: success, not failure: the launch path turns a failure into RepatchFailed and
        // refuses to start the game, and an unpatchable file must never cost the player the game.
        result.IsSuccess.ShouldBeTrue();
        result.Value.AppliedTranslations.ShouldBe(0);
        result.Value.SkippedTranslations.ShouldBe(1);
        result.Value.MissingSourceDigestTranslations.ShouldBe(1);
        result.Value.Warnings.ShouldContain(warning => warning.Contains("no source_digest column"));
    }

    [Fact]
    public void ApplyTranslations_RowWithoutASourceDigest_ShouldNotEvenLoadTheSubFile()
    {
        // Arrange: a wholly six-column artifact is ~800k unpatchable rows; deciding that before the
        // subfile load is what keeps it from costing a full corpus read for nothing.
        SetupAllPassingDefaults();
        SetupTranslations(CreateSixColumnTranslation());

        // Act
        _sut.ApplyTranslations("/translations/polish.txt", "/game/client_local_English.dat");

        // Assert
        _datFileHandler.DidNotReceive().GetSubfileData(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>());
    }

    [Fact]
    public void ApplyTranslations_ManyGuardSkips_ShouldReportACountAndABoundedSample()
    {
        // Arrange: a major update can move thousands of sources at once (U49 moved 1,644). Both
        // consumers of Warnings print or log it line by line, so the bound has to be in the summary.
        SetupAllPassingDefaults();
        // 120 fragments, not more: the fixture writes the fragment count as a single VarLen byte.
        _datFileHandler.GetSubfileData(DatHandle, TextFileId, 100)
            .Returns(Result.Success(TestDataFactory.CreateTextSubFileData(TextFileId, FragmentId1, 120)));

        Translation[] moved = Enumerable.Range(0, 120)
            .Select(index => CreateTranslation(
                gossipId: FragmentId1 + (ulong)index,
                sourceDigest: SourceDigest.ForExportForm("Some other English", 0)))
            .ToArray();
        SetupTranslations(moved);

        // Act
        Result<PatchSummaryResponse> result = _sut.ApplyTranslations("/translations/polish.txt", "/game/client_local_English.dat");

        // Assert: the per-row samples are the ones that start with "Fragment"; the roll-up line and
        // the "and N more" tail are separate entries.
        result.Value.SourceMovedTranslations.ShouldBe(120);
        result.Value.Warnings.Count(warning => warning.StartsWith("Fragment ")).ShouldBe(100);
        result.Value.Warnings.ShouldContain(warning => warning.Contains("... and 20 more"));
    }

    [Fact]
    public void ApplyTranslations_RowWrittenIntoASubFileThatFailedToCommit_ShouldNotBeRecordedInTheLedger()
    {
        // Arrange: the ledger claims what the DAT holds. Recording a row whose subfile never
        // reached disk would admit a later write against text that is not there (ADR-0047 §4).
        SetupAllPassingDefaults();
        _datFileHandler.PutSubfileData(DatHandle, TextFileId, Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(Result.Failure(new Error("DatFile.WriteError", "Disk full", ErrorType.IoError)));
        SetupTranslations(CreateTranslation());

        // Act
        _sut.ApplyTranslations("/translations/polish.txt", "/game/client_local_English.dat");

        // Assert
        _translationLedger.DidNotReceive().Save(
            Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<LedgerKey, string>>());
    }

    [Fact]
    public void ApplyTranslations_RowsAbsentFromTheArtifact_ShouldKeepTheirLedgerEntries()
    {
        // Arrange: the ledger is UPSERTED, never rebuilt (ADR-0047 §4). A row edited back to Draft
        // leaves the artifact, but its Polish still sits on the fragment; dropping the entry would
        // strand that fragment on our older Polish once the row is re-approved.
        SetupAllPassingDefaults();
        LedgerKey absentRow = new(TextFileId2, 7777);
        _translationLedger.Read(Arg.Any<string>()).Returns(new Dictionary<LedgerKey, string>
        {
            [absentRow] = SourceDigest.ForExportForm("Polish we wrote in an earlier run", 0)
        });
        SetupTranslations(CreateTranslation());

        // Act
        _sut.ApplyTranslations("/translations/polish.txt", "/game/client_local_English.dat");

        // Assert
        _translationLedger.Received(1).Save(
            Arg.Any<string>(),
            Arg.Is<IReadOnlyDictionary<LedgerKey, string>>(entries => entries.ContainsKey(absentRow)));
    }

    [Fact]
    public void ApplyTranslations_WhenTheLedgerCannotBeWritten_ShouldWarnRatherThanFailThePatch()
    {
        // Arrange: the DAT is already written by then, and a lost ledger only under-patches next
        // time. Failing here would turn a hint's IO error into a refused launch.
        SetupAllPassingDefaults();
        _translationLedger.Save(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<LedgerKey, string>>())
            .Returns(Result.Failure(new Error("TranslationFileSync.CacheWriteError", "Read-only volume", ErrorType.IoError)));
        SetupTranslations(CreateTranslation());

        // Act
        Result<PatchSummaryResponse> result = _sut.ApplyTranslations("/translations/polish.txt", "/game/client_local_English.dat");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.AppliedTranslations.ShouldBe(1);
        result.Value.Warnings.ShouldContain(warning => warning.Contains("Read-only volume"));
    }

    [Fact]
    public void ApplyTranslations_MixedSubFile_ShouldWriteItOnceAndRecordOnlyTheAdmittedRow()
    {
        // Arrange: one subfile, two rows: the first still holds its English (admitted), the second's
        // English moved (refused). The subfile is written back exactly once, and only the admitted
        // row reaches the ledger — a refused row's fragment holds SSG's text, not ours.
        SetupAllPassingDefaults();
        _datFileHandler.GetSubfileData(DatHandle, TextFileId, 100)
            .Returns(Result.Success(TestDataFactory.CreateTextSubFileData(TextFileId, FragmentId1, 2)));
        LedgerKey admittedKey = new(TextFileId, FragmentId1);
        LedgerKey refusedKey = new(TextFileId, FragmentId2);
        SetupTranslations(
            CreateTranslation(gossipId: FragmentId1),
            CreateTranslation(gossipId: FragmentId2, sourceDigest: SourceDigest.ForExportForm("Some other English", 0)));

        // Act
        Result<PatchSummaryResponse> result = _sut.ApplyTranslations("/translations/polish.txt", "/game/client_local_English.dat");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.AppliedTranslations.ShouldBe(1);
        result.Value.SourceMovedTranslations.ShouldBe(1);
        _datFileHandler.Received(1).PutSubfileData(DatHandle, TextFileId, Arg.Any<byte[]>(), Arg.Any<int>(), 1);
        _translationLedger.Received(1).Save(
            Arg.Any<string>(),
            Arg.Is<IReadOnlyDictionary<LedgerKey, string>>(entries => entries.ContainsKey(admittedKey) && !entries.ContainsKey(refusedKey)));
    }

    [Fact]
    public void ApplyTranslations_RerunThatChangesNothingInTheLedger_ShouldNotRewriteIt()
    {
        // Arrange: the ledger already records exactly what this run writes again (a no-op re-run of
        // the same artifact). Rewriting a multi-MB sidecar on every launch would be pure churn.
        SetupAllPassingDefaults();
        _translationLedger.Read(Arg.Any<string>()).Returns(new Dictionary<LedgerKey, string>
        {
            [new LedgerKey(TextFileId, FragmentId1)] = SourceDigest.ForExportForm("Przetlumaczony tekst", 0)
        });
        SetupTranslations(CreateTranslation());

        // Act
        Result<PatchSummaryResponse> result = _sut.ApplyTranslations("/translations/polish.txt", "/game/client_local_English.dat");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        _translationLedger.DidNotReceive().Save(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<LedgerKey, string>>());
    }

    [Fact]
    public void ApplyTranslations_SameRowListedTwice_ShouldLetTheSecondRowSeeTheFirstRowsWrite()
    {
        // Arrange: a hand-made file naming the same fragment twice with different content used to be
        // last-wins. The first row's write sits in the in-memory subfile when the second is judged, so
        // clause (b) has to consult the entries pending for that subfile, not only the ledger on disk.
        SetupAllPassingDefaults();
        SetupTranslations(
            CreateTranslation(content: "Pierwsza wersja"),
            CreateTranslation(content: "Druga wersja"));

        // Act
        Result<PatchSummaryResponse> result = _sut.ApplyTranslations("/translations/polish.txt", "/game/client_local_English.dat");

        // Assert: both admitted, no spurious "source moved", the last write is what the ledger records.
        result.IsSuccess.ShouldBeTrue();
        result.Value.AppliedTranslations.ShouldBe(2);
        result.Value.SourceMovedTranslations.ShouldBe(0);
        _translationLedger.Received(1).Save(
            Arg.Any<string>(),
            Arg.Is<IReadOnlyDictionary<LedgerKey, string>>(entries =>
                entries[new LedgerKey(TextFileId, FragmentId1)] == SourceDigest.ForExportForm("Druga wersja", 0)));
    }
}
