using LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Features.TranslationFiles;

/// <summary>
/// The startup catch-up of ADR-0047 ("Deploy ordering") rests on this check: a wrong "no" leaves an
/// updated CLI patching nothing, a wrong "yes" rebuilds a multi-MB artifact on every restart.
/// </summary>
public sealed class ArtifactFormatStampTests
{
    [Fact]
    public void PredatesSourceDigest_SixColumnArtifact_ShouldAskForRegeneration()
        => ArtifactFormatStamp
            .PredatesSourceDigest("620756992||1001||Witaj||NULL||NULL||1\r\n620756992||1002||Zegnaj||NULL||NULL||1\r\n")
            .ShouldBeTrue();

    [Fact]
    public void PredatesSourceDigest_SevenColumnArtifact_ShouldLeaveItAlone()
        => ArtifactFormatStamp
            .PredatesSourceDigest("620756992||1001||Witaj||NULL||NULL||1||a37cc1683216cd32\r\n")
            .ShouldBeFalse();

    [Fact]
    public void PredatesSourceDigest_EmptyArtifact_ShouldLeaveItAlone()
        => ArtifactFormatStamp.PredatesSourceDigest(string.Empty).ShouldBeFalse();

    [Fact]
    public void PredatesSourceDigest_FirstRowTruncatedByThePrefix_ShouldLeaveItAlone()
        // A row longer than the prefix the caller reads must not look format-less.
        => ArtifactFormatStamp.PredatesSourceDigest("620756992||1001||Bardzo dlugi tekst bez konca").ShouldBeFalse();

    [Fact]
    public void PredatesSourceDigest_ArtifactWhoseFirstRowIsUncarvable_ShouldAskForRegeneration()
        // Not an artifact this version wrote; rebuilding is the only safe reading.
        => ArtifactFormatStamp.PredatesSourceDigest("garbage\r\n").ShouldBeTrue();

    [Fact]
    public void PredatesSourceDigest_ShouldJudgeTheFirstRowNotTheWholePrefix()
    {
        // One projector writes the whole file, so it can never mix formats.
        const string content = "620756992||1001||Witaj||NULL||NULL||1||a37cc1683216cd32\r\n620756992||1002||Zegnaj||NULL||NULL||1\r\n";

        ArtifactFormatStamp.PredatesSourceDigest(content).ShouldBeFalse();
    }
}
