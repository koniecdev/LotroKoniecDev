using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Guards;
using LotroKoniecDev.TranslationSystem.Primitives.Projections;

namespace LotroKoniecDev.TranslationSystem.Projections;

/// <summary>
/// The precomputed translation file for one language (spec 0001): the serialized <c>||</c> content
/// plus its content hash (the HTTP <c>ETag</c>), stored ready-to-serve so the distribution endpoint
/// never rebuilds per request. An app-maintained, derived read projection — refreshed on every write
/// that changes the distributed set (see <c>IPrecomputedTranslationFileProjector</c>) and never a
/// source of truth. It is deliberately a plain <see cref="Entity{TId}"/>, not an
/// <see cref="AggregateRoot{TId}"/>: it guards no business invariant and is only ever blind-upserted
/// by its natural key (one row per language). See ADR-0007.
/// </summary>
public sealed class PrecomputedTranslationFile : Entity<PrecomputedTranslationFileId>
{
    public const int LanguageMaxLength = 8;
    public const int ContentHashLength = 64;

    public string Language { get; }
    public string Content { get; private set; }
    public string ContentHash { get; private set; }
    public DateTimeOffset GeneratedAt { get; private set; }

    /// <summary>
    /// Regenerate-on-write: replaces the serialized content and its hash in place.
    /// </summary>
    public void Refresh(string content, string contentHash, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        Ensure.NotEmpty(now);

        Content = content;
        ContentHash = contentHash;
        GeneratedAt = now;
    }

    public static PrecomputedTranslationFile Create(string language, string content, string contentHash, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        Ensure.NotEmpty(now);

        return new PrecomputedTranslationFile(PrecomputedTranslationFileId.Create(), language, content, contentHash, now);
    }

    private PrecomputedTranslationFile(
        PrecomputedTranslationFileId id,
        string language,
        string content,
        string contentHash,
        DateTimeOffset now) : base(id)
    {
        Language = language;
        Content = content;
        ContentHash = contentHash;
        GeneratedAt = now;
    }

    private PrecomputedTranslationFile()
    {
        Language = null!;
        Content = null!;
        ContentHash = null!;
    }
}
