using LotroKoniecDev.AuthSystem.API.Common;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Common;

public sealed class AbsoluteHttpUriTests
{
    [Theory]
    [InlineData("https://lotro-translator.pl")]
    [InlineData("https://app.lotro-translator.pl/callback")]
    [InlineData("http://localhost:5000")]
    [InlineData("HTTPS://APP.LOTRO-TRANSLATOR.PL/")]
    public void TryParse_ShouldAcceptAnAbsoluteHttpOrHttpsUrl(string value)
    {
        // Act
        bool accepted = AbsoluteHttpUri.TryParse(value, out Uri? uri);

        // Assert
        accepted.ShouldBeTrue();
        uri.ShouldNotBeNull();
    }

    /// <summary>
    /// On Unix a bare path parses as an absolute <c>file://</c> URI, so the scheme check is what
    /// rejects it.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/callback")]
    [InlineData("not a url")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://app.lotro-translator.pl/")]
    public void TryParse_ShouldRejectAnythingElse(string? value)
    {
        // Act
        bool accepted = AbsoluteHttpUri.TryParse(value, out Uri? uri);

        // Assert
        accepted.ShouldBeFalse();
        uri.ShouldBeNull();
    }
}
