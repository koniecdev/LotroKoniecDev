using LotroKoniecDev.Application.Features.TranslationFileSyncing;
using LotroKoniecDev.Primitives.Constants;

namespace LotroKoniecDev.Tests.Unit.Tests.Features;

public sealed class TranslationServerAddressTests
{
    [Theory]
    [InlineData(TmsConstants.DefaultBaseUrl)]
    [InlineData("https://tms.example.com")]
    [InlineData("HTTPS://tms.example.com")]
    [InlineData("http://localhost:5002")]
    [InlineData("http://127.0.0.1:5002")]
    [InlineData("http://[::1]:5002")]
    public void IsAllowed_HttpsOrLoopbackHttp_ReturnsTrue(string value)
    {
        // Act
        bool allowed = TranslationServerAddress.IsAllowed(value);

        // Assert
        allowed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    [InlineData("tms.example.com")]
    [InlineData("/translation-files/pl")]
    [InlineData("ftp://tms.example.com")]
    [InlineData("file:///C:/polish.txt")]
    [InlineData("http://tms.example.com")]
    [InlineData("http://192.168.1.10:5002")]
    [InlineData("http://localhost.evil.com")]
    public void IsAllowed_AnythingElse_ReturnsFalse(string? value)
    {
        // Act
        bool allowed = TranslationServerAddress.IsAllowed(value);

        // Assert
        allowed.ShouldBeFalse();
    }

    [Theory]
    [InlineData("https://tms.example.com/translation-files/pl", true)]
    [InlineData("http://localhost:5002/translation-files/pl", true)]
    [InlineData("http://tms.example.com/translation-files/pl", false)]
    [InlineData("ftp://tms.example.com/polish.txt", false)]
    public void HasAllowedScheme_AbsoluteUri_FollowsTheSameRule(string value, bool expected)
    {
        // Act
        bool allowed = TranslationServerAddress.HasAllowedScheme(new Uri(value));

        // Assert
        allowed.ShouldBe(expected);
    }
}
