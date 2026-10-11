using LotroKoniecDev.Frontend.Components.Pages.ImportExport;
using LotroKoniecDev.TranslationSystem.Contracts.Import;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using CompletedImport = LotroKoniecDev.Frontend.Components.Pages.ImportExport.ImportSummaryRedirect.CompletedImport;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.ImportExport;

/// <summary>
/// The summary and the version that survive the redirect after an import (#933). Only a value this page
/// wrote may put text on it or choose a version.
/// </summary>
public sealed class ImportSummaryRedirectTests
{
    private static readonly Guid GameVersionId = Guid.Parse("0192a000-0000-7000-8000-000000000048");

    private readonly EphemeralDataProtectionProvider _dataProtectionProvider = new();

    [Fact]
    public void Read_ValueFromPageUriWith_ReturnsTheVersionAndTheWholeSummary()
    {
        ImportSummaryRedirect redirect = new(_dataProtectionProvider);
        ImportSummary summary = new(
            Added: 1,
            SourceChanged: 2,
            Invalidated: 3,
            Removed: 4,
            Unchanged: 5,
            Echoed: 6,
            Warnings: ["2 previously-removed row(s) re-added with an unchanged source and restored.", "Żółć"]);

        string pageUri = redirect.PageUriWith(new CompletedImport(GameVersionId, summary));

        CompletedImport read = redirect.Read(SummaryParameterOf(pageUri)).ShouldNotBeNull();
        read.GameVersionId.ShouldBe(GameVersionId);
        read.Summary.Added.ShouldBe(1);
        read.Summary.SourceChanged.ShouldBe(2);
        read.Summary.Invalidated.ShouldBe(3);
        read.Summary.Removed.ShouldBe(4);
        read.Summary.Unchanged.ShouldBe(5);
        read.Summary.Echoed.ShouldBe(6);
        read.Summary.Warnings.ShouldBe(summary.Warnings);
    }

    [Fact]
    public void Read_ValueFromPageUriWithASummaryWithoutNotes_ReturnsTheSummaryWithAnEmptyList()
    {
        // Most imports have no notes, so an empty list must survive the redirect.
        ImportSummaryRedirect redirect = new(_dataProtectionProvider);

        string pageUri = redirect.PageUriWith(new CompletedImport(GameVersionId, new ImportSummary(7, 0, 0, 0, 0, 0, [])));

        CompletedImport read = redirect.Read(SummaryParameterOf(pageUri)).ShouldNotBeNull();
        read.Summary.Added.ShouldBe(7);
        read.Summary.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void PageUriWith_AnyImport_PointsAtTheImportPage()
    {
        ImportSummaryRedirect redirect = new(_dataProtectionProvider);

        string pageUri = redirect.PageUriWith(new CompletedImport(GameVersionId, new ImportSummary(0, 0, 0, 0, 0, 0, [])));

        pageUri.ShouldStartWith("/import-export?summary=");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a protected value")]
    [InlineData("{\"summary\":{\"added\":1,\"warnings\":[\"Kliknij tutaj\"]}}")]
    [InlineData("CfDJ8AAAAAAAAAAAAAAAAAAAAAA")]
    public void Read_ValueThisPageDidNotWrite_ReturnsNull(string? protectedValue)
    {
        ImportSummaryRedirect redirect = new(_dataProtectionProvider);

        CompletedImport? read = redirect.Read(protectedValue);

        read.ShouldBeNull();
    }

    [Fact]
    public void Read_ValueWithOneCharacterChanged_ReturnsNull()
    {
        ImportSummaryRedirect redirect = new(_dataProtectionProvider);
        string protectedValue = SummaryParameterOf(
            redirect.PageUriWith(new CompletedImport(GameVersionId, new ImportSummary(1, 0, 0, 0, 0, 0, []))));
        // A character in the middle: the last one may carry only padding bits, so changing it may change nothing.
        int middle = protectedValue.Length / 2;
        char replacement = protectedValue[middle] == 'A' ? 'B' : 'A';
        string tampered = string.Concat(protectedValue.AsSpan(0, middle), [replacement], protectedValue.AsSpan(middle + 1));

        CompletedImport? read = redirect.Read(tampered);

        read.ShouldBeNull();
    }

    [Fact]
    public void Read_ValueWrittenUnderAnotherKeyRing_ReturnsNull()
    {
        // What a link from before a key ring change looks like: the page shows nothing instead of failing.
        string protectedValue = SummaryParameterOf(
            new ImportSummaryRedirect(new EphemeralDataProtectionProvider())
                .PageUriWith(new CompletedImport(GameVersionId, new ImportSummary(1, 0, 0, 0, 0, 0, []))));
        ImportSummaryRedirect redirect = new(_dataProtectionProvider);

        CompletedImport? read = redirect.Read(protectedValue);

        read.ShouldBeNull();
    }

    [Fact]
    public void Read_ValueProtectedForAnotherPurpose_ReturnsNull()
    {
        string protectedValue = _dataProtectionProvider
            .CreateProtector("LotroKoniecDev.Frontend.SomethingElse")
            .Protect("""{"GameVersionId":"0192a000-0000-7000-8000-000000000048","Summary":{"Added":1,"SourceChanged":0,"Invalidated":0,"Removed":0,"Unchanged":0,"Echoed":0,"Warnings":[]}}""");
        ImportSummaryRedirect redirect = new(_dataProtectionProvider);

        CompletedImport? read = redirect.Read(protectedValue);

        read.ShouldBeNull();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"Added\":1,\"SourceChanged\":0,\"Invalidated\":0,\"Removed\":0,\"Unchanged\":0,\"Echoed\":0,\"Warnings\":[]}")]
    [InlineData("{\"GameVersionId\":\"0192a000-0000-7000-8000-000000000048\"}")]
    [InlineData("{\"GameVersionId\":\"0192a000-0000-7000-8000-000000000048\",\"Summary\":{\"Added\":\"many\"}}")]
    [InlineData("{\"GameVersionId\":\"0192a000-0000-7000-8000-000000000048\",\"Summary\":{\"Added\":1,\"SourceChanged\":0,\"Invalidated\":0,\"Removed\":0,\"Unchanged\":0,\"Echoed\":0,\"Warnings\":null}}")]
    [InlineData("{\"GameVersionId\":\"not a guid\",\"Summary\":{\"Added\":1,\"SourceChanged\":0,\"Invalidated\":0,\"Removed\":0,\"Unchanged\":0,\"Echoed\":0,\"Warnings\":[]}}")]
    public void Read_ValueWithThePurposeButAnotherShape_ReturnsNull(string payload)
    {
        // A link written before the shape of the value changed, if nobody changed the purpose.
        string protectedValue = _dataProtectionProvider
            .CreateProtector(ImportSummaryRedirect.Purpose)
            .Protect(payload);
        ImportSummaryRedirect redirect = new(_dataProtectionProvider);

        CompletedImport? read = redirect.Read(protectedValue);

        read.ShouldBeNull();
    }

    private static string SummaryParameterOf(string pageUri) =>
        QueryHelpers.ParseQuery(new Uri(new Uri("https://localhost"), pageUri).Query)[ImportSummaryRedirect.SummaryParameter]
            .ToString();
}
