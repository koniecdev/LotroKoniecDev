namespace LotroKoniecDev.TranslationSystem.API.Parsing;

/// <summary>
/// One Approved row to serialize into the <c>||</c> translation file. There is no approved column —
/// every distributed row is approved, so the serializer emits the constant <c>1</c>.
/// </summary>
internal sealed record ArtifactRow(int FileId, long GossipId, string Content, string? ArgsOrder, string? ArgsId);

/// <summary>
/// Serializes Approved rows into the LOTRO <c>||</c> contract the patcher's parser consumes. The
/// caller supplies the rows already filtered and sorted; the serializer only formats.
/// </summary>
internal interface ITranslationFileSerializer
{
    string Serialize(IReadOnlyList<ArtifactRow> rows);
}
