using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Infrastructure.Storage;

namespace LotroKoniecDev.Tests.Infrastructure.Tests;

/// <summary>
/// Exercises the write ledger of ADR-0047 §4 against real files. It is the only record of what this
/// patcher put into which fragment, and the guard's second rule is the only way a newer translation can
/// land on a fragment that already holds Polish. So the whole property is: the entries survive a round
/// trip, and a broken file counts as empty instead of throwing.
/// </summary>
public sealed class TranslationLedgerTests : IDisposable
{
    private const string DigestA = "a37cc1683216cd32";
    private const string DigestB = "b37cc1683216cd32";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"lotro-ledger-tests-{Guid.NewGuid():N}");

    private readonly TranslationLedger _sut = new();

    private string TranslationFilePath => Path.Combine(_directory, "translations", "polish.txt");

    private string LedgerPath => TranslationFilePath + ".ledger";

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Read_WithNoLedgerOnDisk_ShouldReturnAnEmptySet()
    {
        // Act: the first-ever run, or a DAT restored next to a fresh translation file.
        IReadOnlyDictionary<LedgerKey, string> entries = _sut.Read(TranslationFilePath);

        // Assert
        entries.ShouldBeEmpty();
    }

    [Fact]
    public void Save_ThenRead_ShouldRoundTripEveryEntry()
    {
        // Arrange: a gossip id past int range, because the column is a ulong in the DAT.
        Dictionary<LedgerKey, string> entries = new()
        {
            [new LedgerKey(620756992, 1001)] = DigestA,
            [new LedgerKey(620756993, 18446744073709551615)] = DigestB
        };

        // Act
        Result saveResult = _sut.Save(TranslationFilePath, entries);
        IReadOnlyDictionary<LedgerKey, string> read = _sut.Read(TranslationFilePath);

        // Assert
        saveResult.IsSuccess.ShouldBeTrue();
        read.Count.ShouldBe(2);
        read[new LedgerKey(620756992, 1001)].ShouldBe(DigestA);
        read[new LedgerKey(620756993, 18446744073709551615)].ShouldBe(DigestB);
    }

    [Fact]
    public void Save_ShouldLandNextToTheTranslationFileLikeTheOtherSidecars()
    {
        // Act: the ledger belongs to the translation file, not to the DAT: a DAT swapped underneath
        // it leaves a ledger describing another file, which under-patches rather than masking.
        _sut.Save(TranslationFilePath, new Dictionary<LedgerKey, string> { [new LedgerKey(1, 2)] = DigestA });

        // Assert
        File.Exists(LedgerPath).ShouldBeTrue();
    }

    [Fact]
    public void Save_TwiceForTheSameKey_ShouldKeepOnlyTheLatestDigest()
    {
        // Arrange: the caller merges and re-saves the whole set every run.
        _sut.Save(TranslationFilePath, new Dictionary<LedgerKey, string> { [new LedgerKey(1, 2)] = DigestA });

        // Act
        _sut.Save(TranslationFilePath, new Dictionary<LedgerKey, string> { [new LedgerKey(1, 2)] = DigestB });

        // Assert
        _sut.Read(TranslationFilePath).ShouldHaveSingleItem().Value.ShouldBe(DigestB);
    }

    [Fact]
    public void Save_ShouldNotLeaveItsTemporaryFileBehind()
    {
        // Act: the swap is temp-file + rename so a crash mid-write leaves the PREVIOUS ledger,
        // never a truncated one the next run would read as a wrong set of entries.
        _sut.Save(TranslationFilePath, new Dictionary<LedgerKey, string> { [new LedgerKey(1, 2)] = DigestA });

        // Assert
        File.Exists(LedgerPath + ".tmp").ShouldBeFalse();
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("1||2")]
    [InlineData("notanumber||2||a37cc1683216cd32")]
    [InlineData("1||notanumber||a37cc1683216cd32")]
    [InlineData("1||2||not-a-digest")]
    [InlineData("1||2||a37cc1683216cd32||extra")]
    public void Read_WithAMalformedLine_ShouldDropThatLineAndKeepTheRest(string malformed)
    {
        // Arrange: the ledger is a hint. Failing the whole read over one bad line would take down
        // the patch (and with it the launch) for a file nothing but this patcher ever writes.
        Directory.CreateDirectory(Path.GetDirectoryName(LedgerPath)!);
        File.WriteAllLines(LedgerPath, [malformed, $"620756992||1001||{DigestA}"]);

        // Act
        IReadOnlyDictionary<LedgerKey, string> entries = _sut.Read(TranslationFilePath);

        // Assert
        entries.ShouldHaveSingleItem().Key.ShouldBe(new LedgerKey(620756992, 1001));
    }

    [Fact]
    public void Read_WithABlankLineInTheFile_ShouldIgnoreIt()
    {
        // Arrange
        Directory.CreateDirectory(Path.GetDirectoryName(LedgerPath)!);
        File.WriteAllLines(LedgerPath, [string.Empty, $"1||2||{DigestA}", "   "]);

        // Act & Assert
        _sut.Read(TranslationFilePath).ShouldHaveSingleItem().Value.ShouldBe(DigestA);
    }

    [Fact]
    public void Save_WithNoEntries_ShouldWriteAnEmptyLedgerThatReadsBackEmpty()
    {
        // Act
        Result saveResult = _sut.Save(TranslationFilePath, new Dictionary<LedgerKey, string>());

        // Assert
        saveResult.IsSuccess.ShouldBeTrue();
        _sut.Read(TranslationFilePath).ShouldBeEmpty();
    }
}
