using System.Text;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.API.Features.Bootstrap;
using LotroKoniecDev.TranslationSystem.API.Parsing;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Repositories;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Repositories;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.Abstractions;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Features.Bootstrap;

public sealed class PolishTranslationSeederTests
{
    private const int FileId = 620756992;

    // The parser is pure and dependency-free, so it runs for real; only the genuine boundaries
    // (repositories, unit of work) are stubbed.
    private readonly ITranslationRepository _translationRepository = Substitute.For<ITranslationRepository>();
    private readonly ITranslatorRepository _translatorRepository = Substitute.For<ITranslatorRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    // The baseline rows the seed sees: the catalog stream projects them, and GetByIdsAsync hands back
    // the very same tracked instances the assertions inspect.
    private readonly List<Translation> _baseline = [];

    private Translator? _insertedSystemTranslator;

    public PolishTranslationSeederTests()
    {
        _translationRepository.StreamCatalogEntriesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => CatalogStream([.. _baseline]));
        _translationRepository.GetByIdsAsync(Arg.Any<IReadOnlyList<TranslationId>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => ResolveByIds(callInfo.Arg<IReadOnlyList<TranslationId>>()));

        // No system translator yet — the seed provisions one; capture it to assert the stamped FK.
        _translatorRepository.GetByIdentityIdAsync(Arg.Any<SharedKernel.StronglyTypedIds.IdentityId>(), Arg.Any<CancellationToken>())
            .Returns(Maybe<Translator>.None);
        _translatorRepository.When(repository => repository.Insert(Arg.Any<Translator>()))
            .Do(callInfo => _insertedSystemTranslator = callInfo.Arg<Translator>());
    }

    private PolishTranslationSeeder CreateSeeder(int seedChunkSize = 5_000)
        => new(
            new TranslationExportParser(),
            _translationRepository,
            _translatorRepository,
            _unitOfWork,
            TimeProvider.System,
            Microsoft.Extensions.Options.Options.Create(new BootstrapSettings { SeedChunkSize = seedChunkSize }),
            NullLogger<PolishTranslationSeeder>.Instance);

    private static Stream Seed(params string[] lines)
        => new MemoryStream(Encoding.UTF8.GetBytes(string.Join('\n', lines)));

    private static string Line(int gossipId, string content) => $"{FileId}||{gossipId}||{content}||NULL||NULL||1";

    private static Translation BaselineRow(int gossipId, string source = "English")
        => Translation.CreateUntranslated(
            FragmentKey.Create(FileId, gossipId).Value,
            TranslationSource.Create(source, null, null).Value,
            GameVersionId.Create(),
            new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)).Value;

    private static Translation ApprovedRow(int gossipId, string polish)
    {
        Translation row = BaselineRow(gossipId);
        TranslatorId author = TranslatorId.Create();
        row.ProvideTranslation(polish, author, new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero));
        row.Approve(author, new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero));
        return row;
    }

    // A polish.txt line resolves to a baseline row (the seed then approves it) — register the row so
    // both the catalog projection and the chunked tracked-load see the same instance.
    private void GivenBaseline(Translation row) => _baseline.Add(row);

    private static async IAsyncEnumerable<StoredTranslationEntry> CatalogStream(params Translation[] rows)
    {
        await Task.Yield();
        foreach (Translation row in rows)
        {
            yield return new StoredTranslationEntry(
                row.Id,
                FragmentKeyValue.From(row.FragmentKey),
                row.Status,
                row.TranslatedText,
                row.IsRemoved);
        }
    }

    private IReadOnlyList<Translation> ResolveByIds(IReadOnlyList<TranslationId> ids)
        => _baseline.Where(row => ids.Contains(row.Id)).ToList();

    [Fact]
    public async Task SeedAsync_WhenLineMatchesBaselineRow_ShouldApproveWithSystemTranslatorAttribution()
    {
        // Arrange
        Translation row = BaselineRow(1);
        GivenBaseline(row);

        // Act
        Result<PolishSeedSummary> result = await CreateSeeder().SeedAsync(Seed(Line(1, "Polski jeden")), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Approved.ShouldBe(1);
        row.Status.ShouldBe(TranslationStatus.Approved);
        row.TranslatedText.ShouldBe("Polski jeden");
        // The seed provisions a system Translator and stamps its local id (ADR-0004), not the bare
        // sentinel IdentityId.
        _insertedSystemTranslator.ShouldNotBeNull();
        _insertedSystemTranslator.IdentityId.ShouldBe(PolishTranslationSeeder.SystemIdentityId);
        _insertedSystemTranslator.DisplayName.Value.ShouldBe(PolishTranslationSeeder.SystemDisplayName);
        row.SubmittedById.ShouldBe(_insertedSystemTranslator.Id);
        row.ApprovedById.ShouldBe(_insertedSystemTranslator.Id);
    }

    [Fact]
    public async Task SeedAsync_WhenLineHasNoBaselineRow_ShouldReportUnmatchedAndNotCreate()
    {
        // Arrange — the repository returns None for every key (default).

        // Act
        Result<PolishSeedSummary> result = await CreateSeeder().SeedAsync(Seed(Line(999, "Polski sierota")), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Approved.ShouldBe(0);
        result.Value.Unmatched.ShouldBe([$"{FileId}/999"]);
        _translationRepository.DidNotReceive().InsertRange(Arg.Any<IEnumerable<Translation>>());
    }

    [Fact]
    public async Task SeedAsync_WhenMatchedAndUnmatchedMixed_ShouldApproveMatchedAndReportUnmatched()
    {
        // Arrange
        Translation row = BaselineRow(1);
        GivenBaseline(row);

        // Act
        Result<PolishSeedSummary> result = await CreateSeeder()
            .SeedAsync(Seed(Line(1, "Polski jeden"), Line(999, "Polski sierota")), CancellationToken.None);

        // Assert
        result.Value.Approved.ShouldBe(1);
        result.Value.Unmatched.ShouldBe([$"{FileId}/999"]);
        row.Status.ShouldBe(TranslationStatus.Approved);
    }

    [Fact]
    public async Task SeedAsync_WhenRowAlreadyApprovedWithSameContent_ShouldSkipAsIdempotent()
    {
        // Arrange
        Translation row = ApprovedRow(1, "Polski jeden");
        DateTimeOffset stampBefore = row.UpdatedAt;
        GivenBaseline(row);

        // Act
        Result<PolishSeedSummary> result = await CreateSeeder().SeedAsync(Seed(Line(1, "Polski jeden")), CancellationToken.None);

        // Assert
        result.Value.Approved.ShouldBe(0);
        result.Value.AlreadyApproved.ShouldBe(1);
        row.UpdatedAt.ShouldBe(stampBefore);
    }

    [Fact]
    public async Task SeedAsync_WhenApprovedRowContentDiffers_ShouldReApproveWithNewContent()
    {
        // Arrange
        Translation row = ApprovedRow(1, "Stary tekst");
        GivenBaseline(row);

        // Act
        Result<PolishSeedSummary> result = await CreateSeeder().SeedAsync(Seed(Line(1, "Nowy tekst")), CancellationToken.None);

        // Assert
        result.Value.Approved.ShouldBe(1);
        result.Value.AlreadyApproved.ShouldBe(0);
        row.TranslatedText.ShouldBe("Nowy tekst");
        row.Status.ShouldBe(TranslationStatus.Approved);
    }

    [Fact]
    public async Task SeedAsync_WhenBaselineRowIsRemoved_ShouldSkipAndNotApprove()
    {
        // Arrange
        Translation row = BaselineRow(1);
        row.MarkRemoved(GameVersionId.Create(), new DateTimeOffset(2026, 6, 3, 0, 0, 0, TimeSpan.Zero));
        GivenBaseline(row);

        // Act
        Result<PolishSeedSummary> result = await CreateSeeder().SeedAsync(Seed(Line(1, "Polski jeden")), CancellationToken.None);

        // Assert
        result.Value.SkippedRemoved.ShouldBe(1);
        result.Value.Approved.ShouldBe(0);
        row.Status.ShouldBe(TranslationStatus.Untranslated);
    }

    [Fact]
    public async Task SeedAsync_WhenFileHasParseError_ShouldFailAndNotPersist()
    {
        // Arrange — the line is missing its trailing fields.
        Stream seed = Seed(Line(1, "Polski jeden"), "620756992||2||truncated line");

        // Act
        Result<PolishSeedSummary> result = await CreateSeeder().SeedAsync(seed, CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Bootstrap.PolishSeedParseFailed");
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedAsync_WhenRowFailsFragmentKeyValidation_ShouldFailAndNotPersist()
    {
        // Arrange — file id 0 parses but fails FragmentKey validation.
        Stream seed = Seed("0||1||Polski jeden||NULL||NULL||1");

        // Act
        Result<PolishSeedSummary> result = await CreateSeeder().SeedAsync(seed, CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Bootstrap.PolishSeedInvalidRow");
    }

    [Fact]
    public async Task SeedAsync_WhenChangedRowsSpanMultipleChunks_ShouldReadCatalogOnceAndLoadTrackedRowsInIdChunks()
    {
        // Arrange — three baseline rows to approve; a chunk size of two forces two id-chunk loads.
        Translation row1 = BaselineRow(1);
        Translation row2 = BaselineRow(2);
        Translation row3 = BaselineRow(3);
        GivenBaseline(row1);
        GivenBaseline(row2);
        GivenBaseline(row3);

        // Act
        Result<PolishSeedSummary> result = await CreateSeeder(seedChunkSize: 2)
            .SeedAsync(Seed(Line(1, "Jeden"), Line(2, "Dwa"), Line(3, "Trzy")), CancellationToken.None);

        // Assert — all three approved, the catalog is read exactly once, and the tracked rows arrive in
        // two id-chunks (ceil(3/2)) rather than one round-trip per line (PERF-06).
        result.Value.Approved.ShouldBe(3);
        row1.Status.ShouldBe(TranslationStatus.Approved);
        row2.Status.ShouldBe(TranslationStatus.Approved);
        row3.Status.ShouldBe(TranslationStatus.Approved);
        _translationRepository.Received(1).StreamCatalogEntriesAsync(Arg.Any<CancellationToken>());
        await _translationRepository.Received(2).GetByIdsAsync(Arg.Any<IReadOnlyList<TranslationId>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedAsync_WhenDuplicateKeyWithDifferentContent_ShouldApproveEachLineAndPersistTheLastWrite()
    {
        // Arrange — one baseline row; the file repeats its key with two different translations. The
        // in-memory view is advanced per line, so the second line sees the first as already Approved.
        Translation row = BaselineRow(1);
        GivenBaseline(row);

        // Act
        Result<PolishSeedSummary> result = await CreateSeeder()
            .SeedAsync(Seed(Line(1, "Pierwsza"), Line(1, "Druga")), CancellationToken.None);

        // Assert — each line counts as an approval (as the old per-line loop did) and the last write wins.
        result.Value.Approved.ShouldBe(2);
        result.Value.AlreadyApproved.ShouldBe(0);
        row.TranslatedText.ShouldBe("Druga");
        row.Status.ShouldBe(TranslationStatus.Approved);
    }

    [Fact]
    public async Task SeedAsync_WhenDuplicateKeyWithIdenticalContent_ShouldApproveOnceAndCountTheRepeatAsAlreadyApproved()
    {
        // Arrange — one baseline row; the file repeats its key with the same translation twice.
        Translation row = BaselineRow(1);
        GivenBaseline(row);

        // Act
        Result<PolishSeedSummary> result = await CreateSeeder()
            .SeedAsync(Seed(Line(1, "Ta sama"), Line(1, "Ta sama")), CancellationToken.None);

        // Assert — the first line approves, the second sees that in-memory approval and is skipped as
        // idempotent (matching the old loop, whose second lookup returned the just-approved row).
        result.Value.Approved.ShouldBe(1);
        result.Value.AlreadyApproved.ShouldBe(1);
        row.TranslatedText.ShouldBe("Ta sama");
        row.Status.ShouldBe(TranslationStatus.Approved);
    }
}
