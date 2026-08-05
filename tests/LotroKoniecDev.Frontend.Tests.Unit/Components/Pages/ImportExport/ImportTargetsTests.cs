using LotroKoniecDev.Frontend.Components.Pages.ImportExport;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.Contracts.GameVersions;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate.Enums;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.ImportExport;

/// <summary>
/// The import page's target-selection rule, extracted so it can be verified at all: Blazor's static-SSR
/// form mapping never serializes a programmatically set file input, so the page's own submit path stops
/// at the "choose a file" guard in bUnit and this logic would otherwise ship untested.
/// <para>
/// The rule is entirely the server's <c>import</c> rel (#610) — the API withholds it for a non-admin and
/// for a <c>Superseded</c> version. Status is deliberately NOT re-interpreted here: a
/// <c>Superseded</c> row that somehow carried the rel would still be offered, because the server, not
/// this page, decides.
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
        // What a non-admin sees, and what an admin sees once every version is Superseded — the page must
        // say so instead of rendering a selector whose every option would be refused on submit.
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
        // The reachable production case: an admin posting against a Superseded version. A null here is a
        // refusal that surfaces as a message — never a path composed from the id.
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
