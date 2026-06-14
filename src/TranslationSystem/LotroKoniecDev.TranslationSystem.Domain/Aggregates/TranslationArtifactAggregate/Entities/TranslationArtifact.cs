using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Guards;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationArtifactAggregate;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationArtifactAggregate.Entities;

/// <summary>
/// The pre-built translation file for one language (spec 0001): the serialized <c>||</c> content
/// plus its content hash (the HTTP <c>ETag</c>). Regenerated on every write that changes the
/// distributed set and served by the distribution endpoint without a per-request rebuild — a
/// derived artifact (one row per language), never a source of truth.
/// </summary>
public sealed class TranslationArtifact : AggregateRoot<TranslationArtifactId>
{
    public const int LanguageMaxLength = 8;
    public const int ContentHashLength = 64;

    public string Language { get; }
    public string Content { get; private set; }
    public string ContentHash { get; private set; }
    public DateTimeOffset GeneratedAt { get; private set; }

    public static TranslationArtifact Create(string language, string content, string contentHash, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        Ensure.NotEmpty(now);

        return new TranslationArtifact(TranslationArtifactId.Create(), language, content, contentHash, now);
    }

    /// <summary>
    /// Regenerate-on-write: replaces the serialized content and its hash in place.
    /// </summary>
    public void Replace(string content, string contentHash, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        Ensure.NotEmpty(now);

        Content = content;
        ContentHash = contentHash;
        GeneratedAt = now;
    }

    private TranslationArtifact(
        TranslationArtifactId id,
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

    private TranslationArtifact()
    {
        Language = null!;
        Content = null!;
        ContentHash = null!;
    }
}
