using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Guards;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Entities;

/// <summary>
/// The TMS-side profile of a user who edits translations, keyed by the AuthSystem's
/// <see cref="IdentityId"/> (ADR-0004). It is created on the first authenticated request, safely twice,
/// and refreshed from the claims when they change, so no separate sync is needed (ADR-0004, amended
/// 2026-06-24).
/// </summary>
public sealed class Translator : AggregateRoot<TranslatorId>
{
    /// <summary>
    /// What the editor shows as the author once the account behind the profile is erased.
    /// </summary>
    public const string ErasedDisplayName = "Usunięte konto";

    public IdentityId IdentityId { get; }
    public DisplayName DisplayName { get; private set; }
    public Email? Email { get; private set; }
    public DateTimeOffset ProvisionedAt { get; }

    /// <summary>
    /// A <c>null</c> <paramref name="email"/> clears an address we knew before.
    /// </summary>
    public void RefreshProfile(DisplayName displayName, Email? email)
    {
        ArgumentNullException.ThrowIfNull(displayName);

        DisplayName = displayName;
        Email = email;
    }

    /// <summary>
    /// Called once the AuthSystem has erased the account (ADR-0065). The profile and its translations
    /// stay, but the profile no longer says whose it was. Doing it twice changes nothing.
    /// </summary>
    public void Erase()
    {
        DisplayName = DisplayName.Create(ErasedDisplayName).Value;
        Email = null;
    }

    public static Result<Translator> Create(
        IdentityId identityId,
        DisplayName displayName,
        Email? email,
        DateTimeOffset now)
    {
        Ensure.NotEmpty(identityId);
        ArgumentNullException.ThrowIfNull(displayName);
        Ensure.NotEmpty(now);

        Translator instance = new(TranslatorId.Create(), identityId, displayName, email, now);

        return Result.Success(instance);
    }

    private Translator(
        TranslatorId id,
        IdentityId identityId,
        DisplayName displayName,
        Email? email,
        DateTimeOffset now) : base(id)
    {
        IdentityId = identityId;
        DisplayName = displayName;
        Email = email;
        ProvisionedAt = now;
    }

    private Translator()
    {
        DisplayName = null!;
    }
}
