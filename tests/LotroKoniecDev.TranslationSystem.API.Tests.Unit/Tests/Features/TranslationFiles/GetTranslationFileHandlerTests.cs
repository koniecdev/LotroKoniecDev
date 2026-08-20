using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.ReadDbContexts;
using NSubstitute;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Features.TranslationFiles;

public sealed class GetTranslationFileHandlerTests
{
    [Fact]
    public async Task Handle_WhenLanguageUnsupported_ShouldReturnValidationError()
    {
        // Arrange: an unsupported language is rejected before the read model is touched.
        GetTranslationFile.Handler handler = new(Substitute.For<IApplicationReadDbContext>());

        // Act
        Result<GetTranslationFile.TranslationFileResult> result =
            await handler.Handle(new GetTranslationFile.Query("de"), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("TranslationFiles.UnsupportedLanguage");
    }
}
