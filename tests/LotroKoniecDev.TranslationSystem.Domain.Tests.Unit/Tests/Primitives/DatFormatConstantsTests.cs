using LotroKoniecDev.TranslationSystem.Primitives.Constants;

namespace LotroKoniecDev.TranslationSystem.Domain.Tests.Unit.Tests.Primitives;

public sealed class DatFormatConstantsTests
{
    [Fact]
    public void Placeholder_ShouldBeTheFileContractMarker()
    {
        // Assert: the translators type it and the file carries it; the string itself is the contract.
        DatFormatConstants.Placeholder.ShouldBe("<--DO_NOT_TOUCH!-->");
    }
}
