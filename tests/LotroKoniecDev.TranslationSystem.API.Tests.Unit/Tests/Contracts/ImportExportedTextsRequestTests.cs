using LotroKoniecDev.TranslationSystem.Contracts.Import;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Contracts;

public sealed class ImportExportedTextsRequestTests
{
    [Fact]
    public void ToQueryParameters_WhenMassRemovalIsAllowed_WritesTheOption()
    {
        // Pinned as text: it is the API's public option name (#1478).
        IReadOnlyList<KeyValuePair<string, string?>> parameters =
            new ImportExportedTextsRequest(AllowMassRemoval: true).ToQueryParameters();

        parameters.ShouldBe([new KeyValuePair<string, string?>("allowMassRemoval", "true")]);
    }

    [Fact]
    public void ToQueryParameters_AtTheDefault_WritesNothing()
        => new ImportExportedTextsRequest().ToQueryParameters().ShouldBeEmpty();
}
