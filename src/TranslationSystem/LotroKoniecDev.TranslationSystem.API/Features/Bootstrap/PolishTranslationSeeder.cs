using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.API.Parsing;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Repositories;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Repositories;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.Abstractions;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LotroKoniecDev.TranslationSystem.API.Features.Bootstrap;

/// <summary>
/// Seeds the existing production <c>polish.txt</c> as Approved translations on top of the imported
/// baseline (spec 0001, #28). It matches each line to a baseline row by <c>(FileId, GossipId)</c>
/// and provides+approves the Polish content, stamping a well-known system principal. It is
/// merge-only — a line with no baseline row is reported, never inserted — and idempotent: an
/// already-approved identical row is left untouched, so re-running updates and never duplicates.
/// The whole catalog is read once into an in-memory view and every line is decided from memory
/// (PERF-06): only the rows that actually change are then reloaded as tracked aggregates in bounded
/// id-chunks, so the round-trips scale with the changed set, not the file.
/// </summary>
internal sealed class PolishTranslationSeeder : IPolishTranslationSeeder
{
    /// <summary>
    /// Well-known system principal stamped as submitter and approver on bootstrap-seeded rows: the
    /// existing production translations predate the editor loop and have no interactive author. The
    /// sentinel GUID (mnemonic <c>5EED</c>) is deliberately recognizable and never collides with an
    /// OpenIddict-issued user id. It is the <c>IdentityId</c> of the system <c>Translator</c> the seed
    /// provisions (ADR-0004).
    /// </summary>
    public static readonly IdentityId SystemIdentityId =
        IdentityId.Create(new Guid("5eed0000-0000-0000-0000-000000000001"));

    /// <summary>The display name of the system translator the bootstrap seed attributes rows to.</summary>
    public const string SystemDisplayName = "System (bootstrap seed)";

    private readonly ITranslationExportParser _parser;
    private readonly ITranslationRepository _translationRepository;
    private readonly ITranslatorRepository _translatorRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly BootstrapSettings _settings;
    private readonly ILogger<PolishTranslationSeeder> _logger;

    public PolishTranslationSeeder(
        ITranslationExportParser parser,
        ITranslationRepository translationRepository,
        ITranslatorRepository translatorRepository,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        IOptions<BootstrapSettings> settings,
        ILogger<PolishTranslationSeeder> logger)
    {
        _parser = parser;
        _translationRepository = translationRepository;
        _translatorRepository = translatorRepository;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<Result<PolishSeedSummary>> SeedAsync(Stream polishStream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(polishStream);

        ParsedExport parsed = await _parser.ParseAsync(polishStream, cancellationToken);
        if (parsed.HasErrors)
        {
            return Result.Failure<PolishSeedSummary>(
                BootstrapErrors.PolishSeedParseFailed(parsed.Errors.Count, parsed.Errors[0]));
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();

        // Seeded rows are attributed to a local system Translator (ADR-0004) — get-or-create it once,
        // idempotently, before stamping its TranslatorId. The system principal has no claims, so its
        // display name is a fixed constant.
        Result<TranslatorId> systemTranslatorResult = await GetOrCreateSystemTranslatorAsync(now, cancellationToken);
        if (systemTranslatorResult.IsFailure)
        {
            return Result.Failure<PolishSeedSummary>(systemTranslatorResult.Error);
        }

        TranslatorId systemTranslatorId = systemTranslatorResult.Value;

        // One untracked pass over the whole catalog into an in-memory view keyed by fragment identity
        // (PERF-06): every polish.txt line is then decided from memory, replacing the per-line
        // GetByFragmentKeyAsync round-trip (tens of thousands during bootstrap).
        Dictionary<FragmentKeyValue, StoredTranslationEntry> catalog = await LoadCatalogAsync(cancellationToken);

        int approved = 0;
        int alreadyApproved = 0;
        int skippedRemoved = 0;
        List<string> unmatched = [];
        Dictionary<TranslationId, string> approvalsById = [];

        foreach (ParsedExportRow row in parsed.Rows)
        {
            Result<FragmentKey> keyResult = FragmentKey.Create(row.FileId, row.GossipId);
            if (keyResult.IsFailure)
            {
                return Result.Failure<PolishSeedSummary>(
                    BootstrapErrors.PolishSeedInvalidRow(row.FileId, row.GossipId, keyResult.Error.Message));
            }

            FragmentKey key = keyResult.Value;
            FragmentKeyValue keyValue = FragmentKeyValue.From(key);

            // Merge-only (#28): a line without a baseline row is reported, never inserted.
            if (!catalog.TryGetValue(keyValue, out StoredTranslationEntry entry))
            {
                unmatched.Add(key.ToString());
                continue;
            }

            // A soft-removed row is out of the distributed set and cannot be approved; leave it be.
            if (entry.IsRemoved)
            {
                skippedRemoved++;
                continue;
            }

            // Idempotent re-run: an identical already-approved row needs no write.
            if (entry.Status is TranslationStatus.Approved
                && string.Equals(entry.TranslatedText, row.Content, StringComparison.Ordinal))
            {
                alreadyApproved++;
                continue;
            }

            // Any other matched state is queued to be (re)written to the seeded Polish and approved
            // under the system principal (the bootstrap targets a fresh/empty DB (#28), so in practice
            // this only ever fills Untranslated baseline rows). The in-memory view is advanced so a
            // later duplicate line for the same key sees this outcome — exactly as the old per-line
            // loop saw its own tracked mutation — and the last write wins on the persisted row.
            approved++;
            catalog[keyValue] = entry with { Status = TranslationStatus.Approved, TranslatedText = row.Content };
            approvalsById[entry.Id] = row.Content;
        }

        Result applyResult = await ApplyApprovalsInChunksAsync(approvalsById, systemTranslatorId, now, cancellationToken);
        if (applyResult.IsFailure)
        {
            return Result.Failure<PolishSeedSummary>(applyResult.Error);
        }

        PolishSeedSummary summary = new(approved, alreadyApproved, skippedRemoved, unmatched);
        _logger.LogInformation(
            "Polish seed complete: {Approved} approved, {AlreadyApproved} already approved, "
            + "{SkippedRemoved} skipped (removed), {Unmatched} unmatched.",
            summary.Approved, summary.AlreadyApproved, summary.SkippedRemoved, summary.Unmatched.Count);

        return Result.Success(summary);
    }

    /// <summary>
    /// Streams the whole catalog once into an in-memory view keyed by fragment identity (PERF-06),
    /// the single read that replaces the old per-line lookup.
    /// </summary>
    private async Task<Dictionary<FragmentKeyValue, StoredTranslationEntry>> LoadCatalogAsync(CancellationToken cancellationToken)
    {
        Dictionary<FragmentKeyValue, StoredTranslationEntry> catalog = [];
        await foreach (StoredTranslationEntry entry in _translationRepository.StreamCatalogEntriesAsync(cancellationToken))
        {
            catalog[entry.Key] = entry;
        }

        return catalog;
    }

    /// <summary>
    /// Reloads only the rows the merge changes as tracked aggregates, in bounded id-chunks
    /// (<see cref="BootstrapSettings.SeedChunkSize"/>), and mutates each through the domain methods
    /// before saving and clearing the tracker per chunk — so the working set scales with the changed
    /// set, not the catalog (PERF-06). Each chunk commits on its own; a mid-way failure leaves a
    /// partial seed the idempotent re-run completes. Unlike the import's chunked apply
    /// (<c>ExecuteInTransactionAsync</c>), this deliberately skips a wrapping transaction: the seed has
    /// no bulk-<c>COPY</c> leg to enlist, runs once before the app serves traffic and is idempotent, so
    /// per-chunk commits keep the <c>Approve</c>-failure path a clean <see cref="Result"/>
    /// (no throw through a transaction seam) at no real atomicity cost.
    /// </summary>
    private async Task<Result> ApplyApprovalsInChunksAsync(
        Dictionary<TranslationId, string> approvalsById,
        TranslatorId systemTranslatorId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (approvalsById.Count == 0)
        {
            return Result.Success();
        }

        List<TranslationId> ids = [.. approvalsById.Keys];
        for (int offset = 0; offset < ids.Count; offset += _settings.SeedChunkSize)
        {
            int chunkSize = Math.Min(_settings.SeedChunkSize, ids.Count - offset);
            List<TranslationId> chunk = new(chunkSize);
            for (int index = 0; index < chunkSize; index++)
            {
                chunk.Add(ids[offset + index]);
            }

            IReadOnlyList<Translation> translations = await _translationRepository.GetByIdsAsync(chunk, cancellationToken);
            foreach (Translation translation in translations)
            {
                string translatedText = approvalsById[translation.Id];
                translation.ProvideTranslation(translatedText, systemTranslatorId, now);
                Result approveResult = translation.Approve(systemTranslatorId, now);
                if (approveResult.IsFailure)
                {
                    return approveResult;
                }
            }

            await _unitOfWork.SaveChangesAsync(cancellationToken);
            _unitOfWork.ClearChangeTracker();
        }

        return Result.Success();
    }

    /// <summary>
    /// Idempotently provisions the system <c>Translator</c> the seed attributes rows to (ADR-0004):
    /// returns the existing row's id, or creates it and commits it so the seeded translations can
    /// reference it as a valid local <c>TranslatorId</c>.
    /// </summary>
    private async Task<Result<TranslatorId>> GetOrCreateSystemTranslatorAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        Maybe<Translator> existing = await _translatorRepository.GetByIdentityIdAsync(SystemIdentityId, cancellationToken);
        if (existing.HasValue)
        {
            return Result.Success(existing.Value.Id);
        }

        Result<DisplayName> displayNameResult = DisplayName.Create(SystemDisplayName);
        if (displayNameResult.IsFailure)
        {
            return Result.Failure<TranslatorId>(displayNameResult.Error);
        }

        Result<Translator> createResult = Translator.Create(SystemIdentityId, displayNameResult.Value, email: null, now);
        if (createResult.IsFailure)
        {
            return Result.Failure<TranslatorId>(createResult.Error);
        }

        _translatorRepository.Insert(createResult.Value);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(createResult.Value.Id);
    }
}
