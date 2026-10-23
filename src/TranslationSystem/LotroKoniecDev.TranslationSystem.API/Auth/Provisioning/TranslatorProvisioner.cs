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
/// Gets or creates the caller's profile (ADR-0004). A second call is safe thanks to the unique index on
/// <c>Translators.IdentityId</c> plus a re-read of the committed row when two requests race.
/// It runs on every authenticated request (ADR-0004, amended 2026-06-24), so the id and a fingerprint
/// of the claims are cached in memory (PERF-07): unchanged claims never touch the table, changed claims
/// refresh the profile and rewrite the entry. A failure is never cached.
/// </summary>
internal sealed class TranslatorProvisioner : ITranslatorProvisioner
{
    private const string CacheKeyPrefix = "translator-provisioning:";

    /// <summary>
    /// The same as the DI default, written out so the lifetime is visible where it is used.
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

        // Checked before the cache is used, so a token we cannot provision never leaves an entry.
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
                return Result.Success(TranslatorId.FromValue(cached.TranslatorId));
            }

            // The claims changed since the entry was written: refresh from the live row and rewrite it.
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
            // The factory throws so HybridCache drops the entry instead of caching a failure.
            return Result.Failure<TranslatorId>(exception.Error);
        }
    }

    /// <summary>
    /// Throws <see cref="TranslatorProvisioningFailedException"/> on failure, so the cache drops the
    /// entry. A <see cref="DbUpdateException"/> from the write passes through unchanged.
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
    /// Its own scope (#435): HybridCache shares one factory run with every caller of the key, and the
    /// request that started it may be cancelled and dispose its scope while others wait. Owning the unit
    /// of work also keeps this save out of a caller's pending changes.
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

            // The real check behind the fingerprint: a plain repeat visit never writes.
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
            // Another request inserted the row first. Detach our rejected insert, or the save below would
            // send it again, then refresh the committed row.
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
    /// A malformed claim gives no e-mail instead of failing the write: the address is not needed to
    /// credit someone's work.
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
    /// Covers exactly the claims the profile is built from. Roles are left out: they never change the
    /// row, so they would only cause needless misses. The separator keeps ("ab", "c") apart from
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
/// Kept small and JSON-serializable for <see cref="HybridCache"/>.
/// </summary>
internal sealed record CachedProvisioning(Guid TranslatorId, string ClaimsFingerprint);

/// <summary>
/// Carries an <see cref="Error"/> out of the <see cref="HybridCache"/> factory, which drops the entry
/// when the factory throws, so a failure is never stored.
/// </summary>
internal sealed class TranslatorProvisioningFailedException : Exception
{
    public TranslatorProvisioningFailedException(Error error)
    {
        Error = error;
    }

    public Error Error { get; }
}
