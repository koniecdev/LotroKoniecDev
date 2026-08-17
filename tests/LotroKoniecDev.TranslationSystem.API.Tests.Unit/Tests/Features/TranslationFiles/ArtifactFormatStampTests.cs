using LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Features.TranslationFiles;

/// <summary>
/// The startup catch-up of ADR-0047 (Consequences — "Deploy ordering") turns on this one predicate:
/// without it an upgraded CLI downloads a six-column artifact and patches nothing until somebody
/// happens to approve a row; with it firing wrongly, every restart rebuilds a multi-MB artifact.
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
        // No rows means no format to be behind, and regenerating it every start would be pure noise.
        => ArtifactFormatStamp.PredatesSourceDigest(string.Empty).ShouldBeFalse();

    [Fact]
    public void PredatesSourceDigest_FirstRowTruncatedByThePrefix_ShouldLeaveItAlone()
        // The caller reads a bounded prefix so the multi-MB column never reaches the startup path.
        // A row longer than that prefix must not be mistaken for a format-less one, or a current
        // artifact would be rebuilt on every single restart.
        => ArtifactFormatStamp.PredatesSourceDigest("620756992||1001||Bardzo dlugi tekst bez konca").ShouldBeFalse();

    [Fact]
    public void PredatesSourceDigest_ArtifactWhoseFirstRowIsUncarvable_ShouldAskForRegeneration()
        // Anything the carver refuses is not an artifact this version wrote; rebuilding is the only
        // safe reading, and it is idempotent.
        => ArtifactFormatStamp.PredatesSourceDigest("garbage\r\n").ShouldBeTrue();

    [Fact]
    public void PredatesSourceDigest_ShouldJudgeTheFirstRowNotTheWholePrefix()
    {
        // A mixed artifact cannot exist — one projector writes the whole file — so reading further
        // would only cost a scan of the prefix for an answer the first row already gives.
        const string content = "620756992||1001||Witaj||NULL||NULL||1||a37cc1683216cd32\r\n620756992||1002||Zegnaj||NULL||NULL||1\r\n";

        ArtifactFormatStamp.PredatesSourceDigest(content).ShouldBeFalse();
    }
}
