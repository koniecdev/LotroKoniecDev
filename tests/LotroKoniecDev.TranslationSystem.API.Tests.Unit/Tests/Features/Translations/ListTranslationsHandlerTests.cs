using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.API.Features.Translations;
using LotroKoniecDev.TranslationSystem.Contracts.Common;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.ReadDbContexts;
using NSubstitute;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Features.Translations;

public sealed class ListTranslationsHandlerTests
{
    // The read DbContext is the only boundary; on the branches tested here it is never queried,
    // so a bare substitute suffices. Filter/search/sort behavior is data-dependent and lives in
    // the integration suite (real PostgreSQL).
    private readonly IApplicationReadDbContext _readDbContext = Substitute.For<IApplicationReadDbContext>();

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(7, 7)]
    public void Query_Page_IsClampedToAtLeastOne(int input, int expected)
        => new ListTranslations.Query(null, null, null, Page: input).Page.ShouldBe(expected);

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(101, 100)]
    [InlineData(50, 50)]
    public void Query_PageSize_IsClampedBetweenOneAndHundred(int input, int expected)
        => new ListTranslations.Query(null, null, null, PageSize: input).PageSize.ShouldBe(expected);

    [Fact]
    public async Task Handle_WhenLanguageUnsupported_ShouldReturnValidationError()
    {
        // Arrange
        ListTranslations.Handler handler = new(_readDbContext);

        // Act
        Result<PaginationResponse<TranslationListItemResponse>> result =
            await handler.Handle(new ListTranslations.Query("de", null, null), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Translations.UnsupportedLanguage");
    }
}
