using LotroKoniecDev.Application.Features.TranslationFileSyncing;

namespace LotroKoniecDev.Tests.Unit.Tests.Features;

public sealed class TranslationFileContentIntegrityTests
{
    private const string Content = "polish content";

    /// <summary>Independently computed (shell <c>shasum -a 256</c>), never via the code under test.</summary>
    private const string ContentHash = "579BDE6E87308282DEA0FCB1A3E8AF668BF6F558CC4545457C696EFB75F7FD18";

    [Theory]
    [InlineData($"\"{ContentHash}\"")]
    [InlineData("\"579bde6e87308282dea0fcb1a3e8af668bf6f558cc4545457c696efb75f7fd18\"")]
    [InlineData(ContentHash)]
    public void Matches_ETagCarryingTheBodyHash_ShouldBeTrue(string eTag)
    {
        // Act
        bool matches = TranslationFileContentIntegrity.Matches(Content, eTag);

        // Assert — quoted or bare, upper- or lowercase hex: all are the same strong validator.
        matches.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\"DEADBEEF\"")]
    [InlineData($"W/\"{ContentHash}\"")]
    public void Matches_MissingMismatchedOrWeakETag_ShouldBeFalse(string? eTag)
    {
        // Act
        bool matches = TranslationFileContentIntegrity.Matches(Content, eTag);

        // Assert — an unverifiable (missing/weak) validator must fail closed, like a wrong hash.
        matches.ShouldBeFalse();
    }

    [Fact]
    public void Matches_TamperedContentAgainstTheOriginalHash_ShouldBeFalse()
    {
        // Act
        bool matches = TranslationFileContentIntegrity.Matches("polish content tampered", $"\"{ContentHash}\"");

        // Assert
        matches.ShouldBeFalse();
    }

    [Fact]
    public void Matches_ContentWithPolishDiacritics_ShouldHashTheUtf8Bytes()
    {
        // Act — hash computed independently over the UTF-8 bytes of the text.
        bool matches = TranslationFileContentIntegrity.Matches(
            "Witaj w Śródziemiu!",
            "\"8D58291BE990D0EAD8E30D5F350961AF361C1E15E5ACB892DE796C2B22AAA2FA\"");

        // Assert
        matches.ShouldBeTrue();
    }

    [Fact]
    public void Matches_EmptyContentWithTheEmptyHash_ShouldBeTrue()
    {
        // Act — SHA-256 of zero bytes; an empty artifact is still a verifiable artifact.
        bool matches = TranslationFileContentIntegrity.Matches(
            string.Empty,
            "\"E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855\"");

        // Assert
        matches.ShouldBeTrue();
    }
}
