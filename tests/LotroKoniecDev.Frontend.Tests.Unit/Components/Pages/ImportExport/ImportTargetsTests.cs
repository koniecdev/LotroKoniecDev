using LotroKoniecDev.Frontend.Components.Pages.ImportExport;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.Contracts.GameVersions;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate.Enums;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.ImportExport;

/// <summary>
/// The rule that decides which versions the import page offers, pulled out so it can be tested at all.
/// Blazor's static SSR form binding never sends a file input set from code, so in bUnit the page's own
/// submit stops at the "choose a file" check and this logic would otherwise ship untested.
/// <para>
/// The rule is only the server's <c>import</c> rel (#610), which the API leaves out for anyone who is
/// not an admin and for a superseded version. The status is deliberately not read here: a superseded row
/// that somehow carried the rel would still be offered, because the server decides, not this page.
/// </para>
/// </summary>
public sealed class ImportTargetsTests
{
    private const string ImportHref = "/advertised/import-into/42";

    [Fact]
    public void Importable_WhenSomeVersionsAdvertiseTheRel_KeepsOnlyThose()
    {
        GameVersionResponse importable = Version("48.0", ImportHref);
        GameVersionResponse superseded = Version("47.0", importHref: null);

        IReadOnlyList<GameVersionResponse> result = ImportTargets.Importable([importable, superseded]);

        result.ShouldHaveSingleItem().Version.ShouldBe("48.0");
    }

    [Fact]
    public void Importable_WhenNoVersionAdvertisesTheRel_IsEmptySoThePageOffersNoSelector()
    {
        // What a non-admin sees, and what an admin sees once every version is superseded. The page must
        // say so instead of showing a selector where every option would be refused on submit.
        IReadOnlyList<GameVersionResponse> result =
            ImportTargets.Importable([Version("47.0", importHref: null), Version("46.0", importHref: null)]);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void Importable_WhenTheListIsNull_IsEmpty()
    {
        ImportTargets.Importable(null).ShouldBeEmpty();
    }

    [Fact]
    public void FindImportHref_WhenTheVersionAdvertisesTheRel_ReturnsThatHref()
    {
        GameVersionResponse version = Version("48.0", ImportHref);

        string? href = ImportTargets.FindImportHref([version], version.Id.Value);

        href.ShouldBe(ImportHref);
    }

    [Fact]
    public void FindImportHref_WhenTheVersionWithholdsTheRel_IsNullSoTheUploadIsRefused()
    {
        // The case that really happens: an admin posting against a superseded version. A null here means
        // we refuse and show a message, never that we build a path from the id.
        GameVersionResponse version = Version("47.0", importHref: null);

        ImportTargets.FindImportHref([version], version.Id.Value).ShouldBeNull();
    }

    [Fact]
    public void FindImportHref_WhenTheIdIsNotInTheList_IsNull()
    {
        // A stale form post: the catalog moved between render and submit.
        ImportTargets.FindImportHref([Version("48.0", ImportHref)], Guid.NewGuid()).ShouldBeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FindImportHref_WhenTheListIsNullOrEmpty_IsNull(bool useNull)
    {
        IReadOnlyList<GameVersionResponse>? versions = useNull ? null : [];

        ImportTargets.FindImportHref(versions, Guid.NewGuid()).ShouldBeNull();
    }

    private static GameVersionResponse Version(string version, string? importHref) =>
        new(GameVersionId.Create(), version, DateTimeOffset.UnixEpoch, GameVersionStatus.Unprocessed)
        {
            Links = importHref is null
                ? []
                : [new LinkDto(importHref, Rels.Import, "POST")]
        };
}
