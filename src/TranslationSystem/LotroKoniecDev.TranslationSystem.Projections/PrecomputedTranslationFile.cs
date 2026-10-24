using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Guards;
using LotroKoniecDev.TranslationSystem.Primitives.Projections;

namespace LotroKoniecDev.TranslationSystem.Projections;

/// <summary>
/// The ready-to-serve translation file for one language (spec 0001, ADR-0007), so the distribution
/// endpoint never builds it per request. <see cref="ContentHash"/> is the HTTP <c>ETag</c>. It is
/// derived data, never a source of truth, and holds no business rule, so it is an
/// <see cref="Entity{TId}"/>, not an aggregate root. This type only inserts a language's first row;
/// later refreshes go through <see cref="IPrecomputedTranslationFileStore.TryRefreshAsync"/>.
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
