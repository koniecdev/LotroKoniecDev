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
/// Creates the translator profile when it is first needed, and it is safe to call twice (ADR-0004).
/// It gets or creates a row keyed by the authenticated identity, and refreshes the display name and
/// e-mail from the current claims each time.
/// Calling it twice is safe thanks to the unique index on <c>Translators.IdentityId</c> plus a check
/// that, when two requests create the same profile at once and one hits the unique constraint, reads
/// the committed row again.
///
/// This runs on every authenticated request (ADR-0004, amended 2026-06-24), so the result, the
/// <see cref="TranslatorId"/> plus a fingerprint of the claims, is kept in an in-memory
/// <see cref="HybridCache"/> for a short time (PERF-07). A request whose claims have not changed is
/// answered from memory and never touches the <c>Translators</c> table. When the fingerprint differs,
/// because the account was renamed or its e-mail changed, the cached value is ignored, the profile is
/// refreshed from the live row and the entry is written again. A failure is never cached.
/// </summary>
internal sealed class TranslatorProvisioner : ITranslatorProvisioner
{
    private const string CacheKeyPrefix = "translator-provisioning:";

    /// <summary>
    /// A short lifetime, keyed by identity. Inside that window an authenticated request whose claims
    /// have not changed is answered from memory and skips the <c>Translators</c> query.
    /// The value is written out here as well, following TheKittySaver's pattern of per-consumer entry
    /// options, even though the DI default is the same, so the lifetime is visible where it is used.
    /// </summary>
    private static readonly HybridCacheEntryOptions ShortTtlEntryOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(5),
        LocalCacheExpiration = TimeSpan.FromMinutes(5)
    };

    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly HybridCache _hybridCache;

    public TranslatorProvisioner(
        ICurrentUserAccessor currentUserAccessor,
        IServiceScopeFactory serviceScopeFactory,
        TimeProvider timeProvider,
        HybridCache hybridCache)
    {
        _currentUserAccessor = currentUserAccessor;
        _serviceScopeFactory = serviceScopeFactory;
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

        // The display name comes from the 'name' claim, or from the 'email' claim when that is
        // missing. An authenticated token always carries at least one of them, and if neither is there
        // the value object returns a validation error.
        // It is resolved and checked before the cache is used, so a token we cannot provision never
        // leaves an entry behind.
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
                // The normal case: the cached entry already matches the current claims, so the
                // Translators table is not queried while the entry is alive.
                return Result.Success(TranslatorId.FromValue(cached.TranslatorId));
            }

            // The claims changed since the entry was written, because the account was renamed or its
            // e-mail changed. Ignore the old value, refresh the profile from the live row and write the
            // entry again, so later requests take the fast path.
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
            // A failure must never be cached. The factory rethrows it as this exception, so HybridCache
            // drops the entry and the next request tries the live row again.
            return Result.Failure<TranslatorId>(exception.Error);
        }
    }

    /// <summary>
    /// What runs on a cache miss: it resolves the translator against the database and turns the result
    /// into the pair we cache, the <see cref="TranslatorId"/> plus the fingerprint. On failure it throws
    /// <see cref="TranslatorProvisioningFailedException"/>, so <see cref="HybridCache"/> drops the entry
    /// instead of caching a failure. A <see cref="DbUpdateException"/> from the write path passes
    /// through unchanged.
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
    /// The real database lookup. It returns the existing translator's id, refreshing the profile only
    /// when the claims really changed, or creates a new row and reads the committed row again when two
    /// requests create it at once.
    /// The whole get-or-create runs in its own scope and never in the calling request's (#435, like
    /// #354). HybridCache runs one factory for every caller waiting on the same key, and the request
    /// that started it can be cancelled, which disposes its scope, while the others are still waiting.
    /// A fresh scope keeps the shared lookup alive for them instead of failing on a disposed context.
    /// Owning the unit of work here also means nothing this method tracks or saves ends up in a
    /// caller's pending changes.
    /// </summary>
    private async ValueTask<Result<TranslatorId>> ResolveAsync(
        IdentityId identityId,
        DisplayName displayName,
        Email? email,
        CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _serviceScopeFactory.CreateAsyncScope();
        ITranslatorRepository translatorRepository =
            scope.ServiceProvider.GetRequiredService<ITranslatorRepository>();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        DateTimeOffset now = _timeProvider.GetUtcNow();

        Maybe<Translator> existing = await translatorRepository.GetByIdentityIdAsync(identityId, cancellationToken);
        if (existing.HasValue)
        {
            Translator translator = existing.Value;

            // We write only when the account was really renamed or its e-mail changed, never on a
            // plain repeat visit. This is the real check behind the fingerprint used on the fast path.
            bool claimsChanged = !translator.DisplayName.Equals(displayName)
                                 || !Equals(translator.Email, email);
            if (claimsChanged)
            {
                translator.RefreshProfile(displayName, email);
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }

            return Result.Success(translator.Id);
        }

        Result<Translator> createResult = Translator.Create(identityId, displayName, email, now);
        if (createResult.IsFailure)
        {
            return Result.Failure<TranslatorId>(createResult.Error);
        }

        translatorRepository.Insert(createResult.Value);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Success(createResult.Value.Id);
        }
        catch (DbUpdateException)
        {
            // Another request inserted the row between our read and our save. Drop our rejected insert
            // from change tracking first, or the retry below would send the row the unique index has
            // already refused, then read the committed row and refresh it. The unit of work belongs to
            // this scope, so the rejected insert can never fire again on a caller's save.
            translatorRepository.Detach(createResult.Value);

            Maybe<Translator> raced = await translatorRepository.GetByIdentityIdAsync(identityId, cancellationToken);
            if (raced.HasNoValue)
            {
                throw;
            }

            raced.Value.RefreshProfile(displayName, email);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Success(raced.Value.Id);
        }
    }

    /// <summary>
    /// Reads the optional e-mail from the <c>email</c> claim. A malformed claim gives no e-mail instead
    /// of failing the whole write, because the address is not needed to credit someone's work.
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
    /// Hashes exactly the claims the profile is built from, the display name and the e-mail, into one
    /// fingerprint. When either changes the fingerprint changes, so the cached entry is ignored and the
    /// profile is refreshed. A matching fingerprint means the profile cannot have changed, so the
    /// database query can be skipped.
    /// Roles are left out: they never change the <c>Translator</c> row, so including them would only
    /// cause cache misses for nothing. The separator between the fields keeps ("ab", "c") apart from
    /// ("a", "bc").
    /// </summary>
    private static string ComputeClaimsFingerprint(DisplayName displayName, Email? email)
    {
        string material = string.Concat(displayName.Value, "\u001f", email?.Value ?? string.Empty);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash);
    }
}

/// <summary>
/// What we cache for a provisioned translator: its id plus a fingerprint of the claims it was built
/// from. It stays small and JSON-serializable for <see cref="HybridCache"/>.
/// </summary>
internal sealed record CachedProvisioning(Guid TranslatorId, string ClaimsFingerprint);

/// <summary>
/// Carries a resolution <see cref="Error"/> out of the <see cref="HybridCache"/> factory, so a failure
/// is not stored as a success. <c>HybridCache</c> drops the entry when the factory throws, and the next
/// request tries the live lookup again.
/// </summary>
internal sealed class TranslatorProvisioningFailedException : Exception
{
    public TranslatorProvisioningFailedException(Error error)
    {
        Error = error;
    }

    public Error Error { get; }
}
