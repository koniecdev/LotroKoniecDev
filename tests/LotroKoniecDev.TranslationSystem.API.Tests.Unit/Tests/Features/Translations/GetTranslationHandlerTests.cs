using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.API.Features.Translations;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.ReadDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using NSubstitute;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Features.Translations;

public sealed class GetTranslationHandlerTests
{
    [Fact]
    public async Task Handle_WhenIdIsEmpty_ShouldReturnNotFound()
    {
        // Arrange — an all-zeros id is short-circuited before the read model is queried.
        GetTranslation.Handler handler = new(Substitute.For<IApplicationReadDbContext>());

        // Act
        Result<GetTranslation.QueryResult> result =
            await handler.Handle(new GetTranslation.Query(TranslationId.Empty), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("TranslationEntity.NotFound");
    }
}
