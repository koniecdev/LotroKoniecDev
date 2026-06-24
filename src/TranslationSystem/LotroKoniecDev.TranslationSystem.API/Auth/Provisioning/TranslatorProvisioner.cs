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

namespace LotroKoniecDev.TranslationSystem.API.Auth.Provisioning;

/// <summary>
/// Lazy idempotent translator provisioning (ADR-0004): get-or-create keyed by the authenticated
/// identity, refreshing the display name / email from the current claims on each touch. Idempotency
/// is guaranteed by the unique index on <c>Translators.IdentityId</c> plus a get-then-create guard
/// that, on a concurrent first-write race (unique-constraint violation), re-reads the committed row.
/// </summary>
internal sealed class TranslatorProvisioner : ITranslatorProvisioner
{
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly ITranslatorRepository _translatorRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public TranslatorProvisioner(
        ICurrentUserAccessor currentUserAccessor,
        ITranslatorRepository translatorRepository,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _currentUserAccessor = currentUserAccessor;
        _translatorRepository = translatorRepository;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
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
        Result<DisplayName> displayNameResult = DisplayName.Create(
            _currentUserAccessor.Username ?? _currentUserAccessor.Email ?? string.Empty);
        if (displayNameResult.IsFailure)
        {
            return Result.Failure<TranslatorId>(displayNameResult.Error);
        }

        Email? email = ResolveEmail();
        DateTimeOffset now = _timeProvider.GetUtcNow();

        Maybe<Translator> existing = await _translatorRepository.GetByIdentityIdAsync(identityId, cancellationToken);
        if (existing.HasValue)
        {
            Translator translator = existing.Value;

            // Steady state on the hot path: the row exists and the claims are unchanged, so there is
            // nothing to write. Provisioning now runs on every authenticated request (ADR-0004
            // amendment 2026-06-24), so it must be a pure read here — a write fires only when an
            // account was actually renamed, never on a plain re-touch.
            bool claimsChanged = !translator.DisplayName.Equals(displayNameResult.Value)
                                 || !Equals(translator.Email, email);
            if (claimsChanged)
            {
                translator.RefreshProfile(displayNameResult.Value, email, now);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
            }

            return Result.Success(translator.Id);
        }

        Result<Translator> createResult = Translator.Create(identityId, displayNameResult.Value, email, now);
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

            raced.Value.RefreshProfile(displayNameResult.Value, email, now);
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
}
