namespace LotroKoniecDev.TranslationSystem.API.Parsing;

/// <summary>
/// No approved flag: every distributed row is approved, so the serializer always writes <c>1</c>.
/// </summary>
/// <param name="SourceDigest">
/// <c>SourceHash.ToWireDigest()</c> of the stored source (ADR-0047). For an approved row that is the
/// English it was approved against, because a source change invalidates the row (spec 0001).
/// </param>
internal sealed record ArtifactRow(
    int FileId,
    long GossipId,
    string Content,
    string? ArgsOrder,
    string? ArgsId,
    string SourceDigest);

/// <summary>
/// The caller passes the rows already filtered and sorted; this only formats them.
/// </summary>
internal interface ITranslationFileSerializer
{
    string Serialize(IReadOnlyList<ArtifactRow> rows);
}
