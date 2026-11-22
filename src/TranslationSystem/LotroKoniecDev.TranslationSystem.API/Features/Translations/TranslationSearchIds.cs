namespace LotroKoniecDev.TranslationSystem.API.Features.Translations;

/// <summary>
/// The ids a search term names. A plain number sets <see cref="FileId"/> and/or <see cref="GossipId"/>,
/// either of which may match. <c>FileId:GossipId</c> sets the pair, which matches one row. All four
/// <see langword="null"/> means a text-only search.
/// </summary>
internal sealed record TranslationSearchIds(int? FileId, long? GossipId, int? PairFileId, long? PairGossipId)
{
    public static readonly TranslationSearchIds None = new(FileId: null, GossipId: null, PairFileId: null, PairGossipId: null);
}
