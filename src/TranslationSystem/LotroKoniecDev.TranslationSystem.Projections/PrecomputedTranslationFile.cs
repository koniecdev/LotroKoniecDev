using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Guards;
using LotroKoniecDev.TranslationSystem.Primitives.Projections;

namespace LotroKoniecDev.TranslationSystem.Projections;

/// <summary>
/// The ready-made translation file for one language (spec 0001): the serialized <c>||</c> content and
/// its content hash, which is the HTTP <c>ETag</c>. It is stored ready to serve, so the distribution
/// endpoint never builds it per request.
/// The app maintains it: it is refreshed after every write that changes the distributed set (see
/// <c>IPrecomputedTranslationFileProjector</c>) and it is never a source of truth. It is a plain
/// <see cref="Entity{TId}"/> and not an <see cref="AggregateRoot{TId}"/> on purpose, because it holds
/// no business rule and is only ever upserted by its natural key, one row per language.
/// The type is immutable. It exists to insert the first row for a language; every later refresh is a
/// single update through <see cref="IPrecomputedTranslationFileStore"/>, which never loads the
/// previous multi-MB content (PERF-04). See ADR-0007.
/// </summary>
public sealed class PrecomputedTranslationFile : Entity<PrecomputedTranslationFileId>
{
    public const int LanguageMaxLength = 8;
    public const int ContentHashLength = 64;

    public string Language { get; }
    public string Content { get; }
    public string ContentHash { get; }
    public DateTimeOffset GeneratedAt { get; }

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
