namespace LotroKoniecDev.TranslationSystem.API.Parsing;

/// <summary>
/// One Approved row to serialize into the <c>||</c> translation file. There is no approved flag —
/// every distributed row is approved, so the serializer emits the constant <c>1</c>.
/// </summary>
/// <param name="SourceDigest">
/// The <c>source_digest</c> column (ADR-0047): <c>SourceHash.ToWireDigest()</c> of the row's stored
/// <c>TranslationSource</c>. An Approved row's stored source IS the English it was approved against
/// — a source change invalidates the row (spec 0001) and approval clears the invalidation — so this
/// is exactly what the patcher must find on the fragment before it may overwrite it.
/// </param>
internal sealed record ArtifactRow(
    int FileId,
    long GossipId,
    string Content,
    string? ArgsOrder,
    string? ArgsId,
    string SourceDigest);

/// <summary>
/// Serializes Approved rows into the LOTRO <c>||</c> contract the patcher's parser consumes. The
/// caller supplies the rows already filtered and sorted; the serializer only formats.
/// </summary>
internal interface ITranslationFileSerializer
{
    string Serialize(IReadOnlyList<ArtifactRow> rows);
}
