namespace LotroKoniecDev.TranslationSystem.API.Parsing;

/// <summary>
/// One approved row on its way into the <c>||</c> translation file. There is no approved flag here,
/// because every distributed row is approved, so the serializer always writes <c>1</c>.
/// </summary>
/// <param name="SourceDigest">
/// The <c>source_digest</c> column (ADR-0047): <c>SourceHash.ToWireDigest()</c> of the row's stored
/// <c>TranslationSource</c>. For an approved row that stored source is the English it was approved
/// against, because a source change invalidates the row (spec 0001) and approving clears that again.
/// So it is exactly what the patcher must find on the fragment before it may overwrite it.
/// </param>
internal sealed record ArtifactRow(
    int FileId,
    long GossipId,
    string Content,
    string? ArgsOrder,
    string? ArgsId,
    string SourceDigest);

/// <summary>
/// Writes approved rows in the LOTRO <c>||</c> format the patcher's parser reads. The caller passes the
/// rows already filtered and sorted; this only formats them.
/// </summary>
internal interface ITranslationFileSerializer
{
    string Serialize(IReadOnlyList<ArtifactRow> rows);
}
