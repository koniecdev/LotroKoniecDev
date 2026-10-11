using LotroKoniecDev.Frontend.Components.Pages.ImportExport;
using LotroKoniecDev.TranslationSystem.Contracts.Import;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.ImportExport;

/// <summary>
/// The summary that survives the redirect after an import (#933). Only a value this page wrote may put
/// text on it.
/// </summary>
public sealed class ImportSummaryRedirectTests
{
    private readonly EphemeralDataProtectionProvider _dataProtectionProvider = new();

    [Fact]
    public void PageUriWith_ThenRead_GivesBackTheWholeSummary()
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

        string pageUri = redirect.PageUriWith(summary);

        ImportSummary read = redirect.Read(SummaryParameterOf(pageUri)).ShouldNotBeNull();
        read.Added.ShouldBe(1);
        read.SourceChanged.ShouldBe(2);
        read.Invalidated.ShouldBe(3);
        read.Removed.ShouldBe(4);
        read.Unchanged.ShouldBe(5);
        read.Echoed.ShouldBe(6);
        read.Warnings.ShouldBe(summary.Warnings);
    }

    [Fact]
    public void PageUriWith_PointsAtTheImportPage()
    {
        ImportSummaryRedirect redirect = new(_dataProtectionProvider);

        string pageUri = redirect.PageUriWith(new ImportSummary(0, 0, 0, 0, 0, 0, []));

        pageUri.ShouldStartWith("/import-export?summary=");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a protected value")]
    [InlineData("{\"added\":1,\"warnings\":[\"Kliknij tutaj\"]}")]
    [InlineData("CfDJ8AAAAAAAAAAAAAAAAAAAAAA")]
    public void Read_ValueThisPageDidNotWrite_ShowsNoSummary(string? protectedSummary)
    {
        ImportSummaryRedirect redirect = new(_dataProtectionProvider);

        ImportSummary? read = redirect.Read(protectedSummary);

        read.ShouldBeNull();
    }

    [Fact]
    public void Read_ValueWithOneCharacterChanged_ShowsNoSummary()
    {
        ImportSummaryRedirect redirect = new(_dataProtectionProvider);
        string protectedSummary = SummaryParameterOf(redirect.PageUriWith(new ImportSummary(1, 0, 0, 0, 0, 0, [])));
        // A character in the middle: the last one may carry only padding bits, so changing it may change nothing.
        int middle = protectedSummary.Length / 2;
        char replacement = protectedSummary[middle] == 'A' ? 'B' : 'A';
        string tampered = string.Concat(protectedSummary.AsSpan(0, middle), [replacement], protectedSummary.AsSpan(middle + 1));

        ImportSummary? read = redirect.Read(tampered);

        read.ShouldBeNull();
    }

    [Fact]
    public void Read_ValueWrittenUnderAnotherKeyRing_ShowsNoSummary()
    {
        // What a link from before a key ring change looks like: the page shows nothing instead of failing.
        string protectedSummary = SummaryParameterOf(
            new ImportSummaryRedirect(new EphemeralDataProtectionProvider())
                .PageUriWith(new ImportSummary(1, 0, 0, 0, 0, 0, [])));
        ImportSummaryRedirect redirect = new(_dataProtectionProvider);

        ImportSummary? read = redirect.Read(protectedSummary);

        read.ShouldBeNull();
    }

    [Fact]
    public void Read_ValueProtectedForAnotherPurpose_ShowsNoSummary()
    {
        string protectedSummary = _dataProtectionProvider
            .CreateProtector("LotroKoniecDev.Frontend.SomethingElse")
            .Protect("""{"Added":1,"SourceChanged":0,"Invalidated":0,"Removed":0,"Unchanged":0,"Echoed":0,"Warnings":[]}""");
        ImportSummaryRedirect redirect = new(_dataProtectionProvider);

        ImportSummary? read = redirect.Read(protectedSummary);

        read.ShouldBeNull();
    }

    private static string SummaryParameterOf(string pageUri) =>
        QueryHelpers.ParseQuery(new Uri(new Uri("https://localhost"), pageUri).Query)[ImportSummaryRedirect.SummaryParameter]
            .ToString();
}
