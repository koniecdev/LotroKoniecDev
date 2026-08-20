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
        // The last line of defence behind UpsertTranslation.Validator and the ProvideTranslation
        // guard: above this length the patcher cannot write the row into the DAT at all (#598,
        // ADR-0043).
        // It is a CHECK and not the varchar(n) that HasMaxLength would emit. Turning text into
        // varchar rewrites the ~780k-row table and rebuilds its trigram index under ACCESS EXCLUSIVE,
        // which is exactly the deploy-time outage ADR-0023 exists to avoid.
        // PostgreSQL's length() counts code points while the DAT counts UTF-16 code units, so this
        // catches the clear violations and the exact measure stays in C#. SourceText needs no limit:
        // it comes out of the DAT and is always short enough.
        builder.ToTable("Translations", table => table.HasCheckConstraint(
            TranslatedTextLengthConstraint,
            $"\"{nameof(Translation.TranslatedText)}\" IS NULL "
            + $"OR length(\"{nameof(Translation.TranslatedText)}\") <= {DatFormatConstants.MaxTranslatedTextLength}"));

        // PostgreSQL's xmin system column gives us a free concurrency token (AUDIT-EF-01). It is
        // mapped as a shadow property, exactly as TheKittySaver's AuditableEntityConfiguration does.
        // An approve or upsert built on an old copy of a row that an import has since changed now
        // fails the version check instead of quietly undoing the invalidation, and
        // DbUpdateConcurrencyExceptionHandler turns that into a 409.
        // Every row already has an xmin, so the migration adds no column. HasColumnType is needed
        // here because "xid" is a PostgreSQL system type that no convention maps.
        builder.Property<uint>(ConcurrencyTokenColumn)
            .HasColumnName(ConcurrencyTokenColumn)
            .HasColumnType(ConcurrencyTokenType)
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        builder.Property(translation => translation.Id)
            .ValueGeneratedNever();

        // (FileId, GossipId) is the natural identity and needs a unique index, so the value object is
        // mapped with OwnsOne. ComplexProperty cannot be indexed in EF Core 10.
        builder.OwnsOne(translation => translation.FragmentKey, ownedBuilder =>
        {
            ownedBuilder.Property(key => key.FileId)
                .HasColumnName(nameof(FragmentKey.FileId));

            ownedBuilder.Property(key => key.GossipId)
                .HasColumnName(nameof(FragmentKey.GossipId));

            ownedBuilder.HasIndex(key => new { key.FileId, key.GossipId })
                .IsUnique();
        });

        // SourceText carries the trigram search index below, so the value object is mapped with
        // OwnsOne. ComplexProperty cannot be indexed in EF Core 10.
        builder.OwnsOne(translation => translation.Source, ownedBuilder =>
        {
            ownedBuilder.Property(source => source.Text)
                .HasColumnName(nameof(Translation.Source) + nameof(TranslationSource.Text));

            ownedBuilder.Property(source => source.ArgsOrder)
                .HasColumnName(nameof(TranslationSource.ArgsOrder));

            ownedBuilder.Property(source => source.ArgsId)
                .HasColumnName(nameof(TranslationSource.ArgsId));

            // A trigram GIN index serves the ILIKE '%term%' search over the English source in
            // ListTranslations.
            ownedBuilder.HasIndex(source => source.Text)
                .HasMethod("gin")
                .HasOperators("gin_trgm_ops");
        });

        // A trigram GIN index serves the ILIKE '%term%' search over the Polish text in
        // ListTranslations.
        builder.HasIndex(translation => translation.TranslatedText)
            .HasMethod("gin")
            .HasOperators("gin_trgm_ops");

        builder.Property(translation => translation.Status)
            .HasConversion<string>()
            .HasMaxLength(EnumConsts.MaxLength);

        // A partial index over the rows that are not removed. The status-filtered list, the stats
        // GROUP BY and the artifact projector's Approved scan all filter on RemovedInVersion IS NULL
        // first.
        builder.HasIndex(translation => translation.Status)
            .HasFilter($"\"{nameof(Translation.RemovedInVersion)}\" IS NULL");

        // Submitter and approver are local foreign keys to Translators (ADR-0004), not the raw Auth
        // IdentityId. The write aggregate holds only the id, because DDD does not navigate from one
        // aggregate root to another. Restrict stops deleting a translator from taking their work with
        // it.
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

        // The version pointers are id-only references to GameVersions (AUDIT-EF-05), like the
        // Translator keys above. Convention gives each key the index the DeleteGameVersion guard
        // scans, since AnyReferencesGameVersionAsync ORs all three columns.
        // Restrict is the database backstop for the gap between that check and the delete: an import
        // that stamps the version in between makes the delete fail instead of leaving rows pointing
        // at a version that is gone.
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
