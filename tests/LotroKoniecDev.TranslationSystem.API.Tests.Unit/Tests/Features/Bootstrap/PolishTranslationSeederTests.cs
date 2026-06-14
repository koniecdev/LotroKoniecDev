using System.Text;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.API.Features.Bootstrap;
using LotroKoniecDev.TranslationSystem.API.Parsing;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Repositories;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.Abstractions;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Features.Bootstrap;

public sealed class PolishTranslationSeederTests
{
    private const int FileId = 620756992;

    // The parser is pure and dependency-free, so it runs for real; only the genuine boundaries
    // (repository, unit of work) are stubbed.
    private readonly ITranslationRepository _translationRepository = Substitute.For<ITranslationRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    public PolishTranslationSeederTests()
    {
        _translationRepository.GetByFragmentKeyAsync(Arg.Any<FragmentKey>(), Arg.Any<CancellationToken>())
            .Returns(Maybe<Translation>.None);
    }

    private PolishTranslationSeeder CreateSeeder()
        => new(
            new TranslationExportParser(),
            _translationRepository,
            _unitOfWork,
            TimeProvider.System,
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
        IdentityId author = IdentityId.Create();
        row.ProvideTranslation(polish, author, new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero));
        row.Approve(author, new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero));
        return row;
    }

    private void GivenBaseline(Translation row)
        => _translationRepository.GetByFragmentKeyAsync(row.FragmentKey, Arg.Any<CancellationToken>())
            .Returns(Maybe<Translation>.From(row));

    [Fact]
    public async Task SeedAsync_WhenLineMatchesBaselineRow_ShouldApproveWithSystemAttribution()
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
        row.SubmittedById.ShouldBe(PolishTranslationSeeder.SystemIdentityId);
        row.ApprovedById.ShouldBe(PolishTranslationSeeder.SystemIdentityId);
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
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
