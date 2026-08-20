using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.API.Features.GameVersions;
using LotroKoniecDev.TranslationSystem.Contracts.GameVersions;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.ReadDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using NSubstitute;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Features.GameVersions;

public sealed class GetGameVersionHandlerTests
{
    [Fact]
    public async Task Handle_WhenIdIsEmpty_ShouldReturnNotFound()
    {
        // Arrange: an all-zeros id is short-circuited before the read model is queried.
        GetGameVersion.Handler handler = new(Substitute.For<IApplicationReadDbContext>());

        // Act
        Result<GameVersionResponse> result =
            await handler.Handle(new GetGameVersion.Query(GameVersionId.Empty), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("GameVersionEntity.NotFound");
    }
}
