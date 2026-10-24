using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Persistence.Consts;
using LotroKoniecDev.TranslationSystem.Primitives.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LotroKoniecDev.TranslationSystem.Persistence.Configurations;

internal sealed class TranslationConfiguration : IEntityTypeConfiguration<Translation>
{
    private const string ConcurrencyTokenColumn = "xmin";
    private const string ConcurrencyTokenType = "xid";
    private const string TranslatedTextLengthConstraint = "CK_Translations_TranslatedText_MaxLength";

    public void Configure(EntityTypeBuilder<Translation> builder)
    {
        // Last line of defence behind UpsertTranslation.Validator and the ProvideTranslation guard
        // (ADR-0043). A CHECK, not varchar(n): changing the type rewrites the ~780k-row table and its
        // trigram index under ACCESS EXCLUSIVE, the deploy outage ADR-0023 avoids. length() counts
        // code points and the DAT counts UTF-16 code units, so the exact check stays in C#.
        // SourceText comes from the DAT, so it needs no limit.
        builder.ToTable("Translations", table => table.HasCheckConstraint(
            TranslatedTextLengthConstraint,
            $"\"{nameof(Translation.TranslatedText)}\" IS NULL "
            + $"OR length(\"{nameof(Translation.TranslatedText)}\") <= {DatFormatConstants.MaxTranslatedTextLength}"));

        // xmin as a free concurrency token (AUDIT-EF-01): an approve or upsert built on a row an
        // import has since changed fails with a 409 (DbUpdateConcurrencyExceptionHandler) instead of
        // undoing the invalidation. HasColumnType is needed because no convention maps "xid".
        builder.Property<uint>(ConcurrencyTokenColumn)
            .HasColumnName(ConcurrencyTokenColumn)
            .HasColumnType(ConcurrencyTokenType)
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        builder.Property(translation => translation.Id)
            .ValueGeneratedNever();

        // OwnsOne, not ComplexProperty, because the natural identity needs a unique index.
        builder.OwnsOne(translation => translation.FragmentKey, ownedBuilder =>
        {
            ownedBuilder.Property(key => key.FileId)
                .HasColumnName(nameof(FragmentKey.FileId));

            ownedBuilder.Property(key => key.GossipId)
                .HasColumnName(nameof(FragmentKey.GossipId));

            ownedBuilder.HasIndex(key => new { key.FileId, key.GossipId })
                .IsUnique();
        });

        // OwnsOne, not ComplexProperty, because SourceText carries the trigram index below.
        builder.OwnsOne(translation => translation.Source, ownedBuilder =>
        {
            ownedBuilder.Property(source => source.Text)
                .HasColumnName(nameof(Translation.Source) + nameof(TranslationSource.Text));

            ownedBuilder.Property(source => source.ArgsOrder)
                .HasColumnName(nameof(TranslationSource.ArgsOrder));

            ownedBuilder.Property(source => source.ArgsId)
                .HasColumnName(nameof(TranslationSource.ArgsId));

            // Serves the ILIKE '%term%' search in ListTranslations.
            ownedBuilder.HasIndex(source => source.Text)
                .HasMethod("gin")
                .HasOperators("gin_trgm_ops");
        });

        // Serves the ILIKE '%term%' search in ListTranslations.
        builder.HasIndex(translation => translation.TranslatedText)
            .HasMethod("gin")
            .HasOperators("gin_trgm_ops");

        builder.Property(translation => translation.Status)
            .HasConversion<string>()
            .HasMaxLength(EnumConsts.MaxLength);

        // The status-filtered list, the stats GROUP BY and the artifact projector's Approved scan all
        // filter on RemovedInVersion IS NULL first.
        builder.HasIndex(translation => translation.Status)
            .HasFilter($"\"{nameof(Translation.RemovedInVersion)}\" IS NULL");

        // Local keys to Translators, not the raw Auth IdentityId (ADR-0004). Restrict stops deleting a
        // translator from taking their work with it.
        builder.Property(translation => translation.SubmittedById)
            .HasColumnName(nameof(Translation.SubmittedById));

        builder.HasOne<Translator>()
            .WithMany()
            .HasForeignKey(translation => translation.SubmittedById)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(translation => translation.ApprovedById)
            .HasColumnName(nameof(Translation.ApprovedById));

        builder.HasOne<Translator>()
            .WithMany()
            .HasForeignKey(translation => translation.ApprovedById)
            .OnDelete(DeleteBehavior.Restrict);

        // Id-only references (AUDIT-EF-05). Convention indexes each key, which the DeleteGameVersion
        // guard needs because AnyReferencesGameVersionAsync ORs all three. Restrict covers the gap
        // between that check and the delete. On PostgreSQL 18 the failure is SQLSTATE 23001
        // (restrict_violation), not 23503, so code that catches it must check 23001 (#1002).
        builder.Property(translation => translation.IntroducedInVersion)
            .HasColumnName(nameof(Translation.IntroducedInVersion));

        builder.HasOne<GameVersion>()
            .WithMany()
            .HasForeignKey(translation => translation.IntroducedInVersion)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(translation => translation.LastSourceChangeInVersion)
            .HasColumnName(nameof(Translation.LastSourceChangeInVersion));

        builder.HasOne<GameVersion>()
            .WithMany()
            .HasForeignKey(translation => translation.LastSourceChangeInVersion)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(translation => translation.RemovedInVersion)
            .HasColumnName(nameof(Translation.RemovedInVersion));

        builder.HasOne<GameVersion>()
            .WithMany()
            .HasForeignKey(translation => translation.RemovedInVersion)
            .OnDelete(DeleteBehavior.Restrict);

        // A get-only property. EF Core skips it unless it is mapped here.
        builder.Property(translation => translation.CreatedAt);
    }
}
