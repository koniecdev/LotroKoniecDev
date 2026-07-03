using System.Security.Cryptography;
using System.Text;
using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Enums;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.API.Auth.CurrentUserAccessing;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Repositories;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.Abstractions;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

namespace LotroKoniecDev.TranslationSystem.API.Auth.Provisioning;

/// <summary>
/// Lazy idempotent translator provisioning (ADR-0004): get-or-create keyed by the authenticated
/// identity, refreshing the display name / email from the current claims on each touch. Idempotency
/// is guaranteed by the unique index on <c>Translators.IdentityId</c> plus a get-then-create guard
/// that, on a concurrent first-write race (unique-constraint violation), re-reads the committed row.
///
/// Because this runs on every authenticated request (ADR-0004 amendment 2026-06-24), the
/// identity → (<see cref="TranslatorId"/> + claims fingerprint) resolution is cached in an L1-only
/// <see cref="HybridCache"/> under a short TTL (PERF-07): a request whose identity-affecting claims
/// are unchanged resolves from memory and never queries the <c>Translators</c> table. A fingerprint
/// mismatch (the account was renamed / its e-mail changed) bypasses the cached value, refreshes the
/// profile against the live row, and re-sets the entry; a resolution failure is never cached.
/// </summary>
internal sealed class TranslatorProvisioner : ITranslatorProvisioner
{
    private const string CacheKeyPrefix = "translator-provisioning:";

    /// <summary>
    /// Short TTL keyed by identity: within the window an authenticated request whose identity-affecting
    /// claims are unchanged resolves entirely from L1 memory, skipping the <c>Translators</c> query.
    /// Restated here at the call site (mirroring TheKittySaver's per-consumer entry-options pattern)
    /// even though the DI default matches it, so the provisioning TTL is explicit where it is used.
    /// </summary>
    private static readonly HybridCacheEntryOptions ShortTtlEntryOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(5),
        LocalCacheExpiration = TimeSpan.FromMinutes(5)
    };

    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly ITranslatorRepository _translatorRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly HybridCache _hybridCache;

    public TranslatorProvisioner(
        ICurrentUserAccessor currentUserAccessor,
        ITranslatorRepository translatorRepository,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        HybridCache hybridCache)
    {
        _currentUserAccessor = currentUserAccessor;
        _translatorRepository = translatorRepository;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
        _hybridCache = hybridCache;
    }

    public async ValueTask<Result<TranslatorId>> ProvisionCurrentAsync(CancellationToken cancellationToken)
    {
        ValueMaybe<IdentityId> maybeIdentity = _currentUserAccessor.MaybeIdentityId;
        if (maybeIdentity.HasNoValue)
        {
            return Result.Failure<TranslatorId>(new Error(
                "Translators.Unauthenticated",
                "The current user identity is required to provision a translator profile.",
                TypeOfError.Forbidden));
        }

        IdentityId identityId = maybeIdentity.Value;

        // The display name is the 'name' claim, falling back to the 'email' claim — an authenticated
        // token always carries at least one; if neither is present the VO surfaces a validation error.
        // Resolved (and validated) before the cache is touched so an unprovisionable token never lands
        // an entry.
        Result<DisplayName> displayNameResult = DisplayName.Create(
            _currentUserAccessor.Username ?? _currentUserAccessor.Email ?? string.Empty);
        if (displayNameResult.IsFailure)
        {
            return Result.Failure<TranslatorId>(displayNameResult.Error);
        }

        DisplayName displayName = displayNameResult.Value;
        Email? email = ResolveEmail();
        string claimsFingerprint = ComputeClaimsFingerprint(displayName, email);
        string cacheKey = CacheKeyPrefix + identityId.Value;

        try
        {
            CachedProvisioning cached = await _hybridCache.GetOrCreateAsync(
                cacheKey,
                (self: this, identityId, displayName, email, claimsFingerprint),
                static (state, ct) => state.self.ResolveAndCacheAsync(
                    state.identityId, state.displayName, state.email, state.claimsFingerprint, ct),
                ShortTtlEntryOptions,
                cancellationToken: cancellationToken);

            if (string.Equals(cached.ClaimsFingerprint, claimsFingerprint, StringComparison.Ordinal))
            {
                // Steady state: the cached entry already reflects the current claims, so the
                // Translators table is not queried within the TTL.
                return Result.Success(TranslatorId.FromValue(cached.TranslatorId));
            }

            // The claims changed since the entry was cached (a renamed account / changed e-mail):
            // bypass the stale value, refresh the profile against the live row, then overwrite the
            // entry so subsequent requests resume the fast path.
            Result<TranslatorId> refreshed = await ResolveAsync(identityId, displayName, email, cancellationToken);
            if (refreshed.IsFailure)
            {
                return refreshed;
            }

            await _hybridCache.SetAsync(
                cacheKey,
                new CachedProvisioning(refreshed.Value.Value, claimsFingerprint),
                ShortTtlEntryOptions,
                cancellationToken: cancellationToken);

            return refreshed;
        }
        catch (TranslatorProvisioningFailedException exception)
        {
            // A resolution failure must never be cached: the factory rethrows it as this sentinel so
            // HybridCache discards the entry and the next request retries against the live row.
            return Result.Failure<TranslatorId>(exception.Error);
        }
    }

    /// <summary>
    /// Cache-miss factory: resolves the translator against the database and projects it into the cached
    /// (<see cref="TranslatorId"/> + fingerprint) tuple. Throws <see cref="TranslatorProvisioningFailedException"/>
    /// on a resolution failure so <see cref="HybridCache"/> discards the entry rather than caching a
    /// failure. A <see cref="DbUpdateException"/> from the write path propagates unchanged.
    /// </summary>
    private async ValueTask<CachedProvisioning> ResolveAndCacheAsync(
        IdentityId identityId,
        DisplayName displayName,
        Email? email,
        string claimsFingerprint,
        CancellationToken cancellationToken)
    {
        Result<TranslatorId> result = await ResolveAsync(identityId, displayName, email, cancellationToken);
        if (result.IsFailure)
        {
            throw new TranslatorProvisioningFailedException(result.Error);
        }

        return new CachedProvisioning(result.Value.Value, claimsFingerprint);
    }

    /// <summary>
    /// The authoritative database resolution: returns the existing translator's id (refreshing its
    /// profile only when the claims actually changed) or creates a new row, re-reading the committed
    /// row on a concurrent first-write race.
    /// </summary>
    private async ValueTask<Result<TranslatorId>> ResolveAsync(
        IdentityId identityId,
        DisplayName displayName,
        Email? email,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        Maybe<Translator> existing = await _translatorRepository.GetByIdentityIdAsync(identityId, cancellationToken);
        if (existing.HasValue)
        {
            Translator translator = existing.Value;

            // A write fires only when an account was actually renamed / had its e-mail changed, never
            // on a plain re-touch — this authoritative check backs the fast-path fingerprint gate.
            bool claimsChanged = !translator.DisplayName.Equals(displayName)
                                 || !Equals(translator.Email, email);
            if (claimsChanged)
            {
                translator.RefreshProfile(displayName, email);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
            }

            return Result.Success(translator.Id);
        }

        Result<Translator> createResult = Translator.Create(identityId, displayName, email, now);
        if (createResult.IsFailure)
        {
            return Result.Failure<TranslatorId>(createResult.Error);
        }

        _translatorRepository.Insert(createResult.Value);

        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Success(createResult.Value.Id);
        }
        catch (DbUpdateException)
        {
            // Concurrent first-write race: another request inserted the row between our read and save.
            // Drop our rejected insert from change tracking first — otherwise the retry below, and the
            // caller's shared unit of work, would re-attempt the row the unique index already rejected
            // — then re-read the committed row and refresh it.
            _translatorRepository.Detach(createResult.Value);

            Maybe<Translator> raced = await _translatorRepository.GetByIdentityIdAsync(identityId, cancellationToken);
            if (raced.HasNoValue)
            {
                throw;
            }

            raced.Value.RefreshProfile(displayName, email);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Success(raced.Value.Id);
        }
    }

    /// <summary>
    /// Resolves the optional email from the <c>email</c> claim. A malformed claim yields no email
    /// rather than failing the whole write — the address is non-essential to attribution.
    /// </summary>
    private Email? ResolveEmail()
    {
        string? emailClaim = _currentUserAccessor.Email;
        if (string.IsNullOrWhiteSpace(emailClaim))
        {
            return null;
        }

        Result<Email> emailResult = Email.Create(emailClaim);

        return emailResult.IsSuccess ? emailResult.Value : null;
    }

    /// <summary>
    /// Hashes the exact claims that determine the provisioned profile — the resolved display name and
    /// e-mail — into a stable fingerprint. A change flips the fingerprint, so the cached entry is
    /// bypassed and the profile refreshed; matching the fingerprint provably means the profile is
    /// unchanged, so the DB query is safely skipped. Roles are excluded: they never alter the
    /// <c>Translator</c> row, so including them would only trigger needless cache misses. The unit
    /// separator keeps ("ab", "c") distinct from ("a", "bc").
    /// </summary>
    private static string ComputeClaimsFingerprint(DisplayName displayName, Email? email)
    {
        string material = string.Concat(displayName.Value, "\u001f", email?.Value ?? string.Empty);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash);
    }
}

/// <summary>
/// The cached projection of a provisioned translator: its stable id plus a fingerprint of the claims
/// that produced it. Kept small and JSON-serializable for <see cref="HybridCache"/>.
/// </summary>
internal sealed record CachedProvisioning(Guid TranslatorId, string ClaimsFingerprint);

/// <summary>
/// Sentinel that bubbles a resolution <see cref="Error"/> out of the <see cref="HybridCache"/> factory
/// so a failure is not cached as success — <c>HybridCache</c> discards the entry when the factory
/// throws, so the next request retries the live resolution.
/// </summary>
internal sealed class TranslatorProvisioningFailedException : Exception
{
    public TranslatorProvisioningFailedException(Error error)
    {
        Error = error;
    }

    public Error Error { get; }
}
