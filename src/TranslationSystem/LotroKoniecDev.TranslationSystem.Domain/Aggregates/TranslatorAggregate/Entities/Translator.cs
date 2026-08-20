using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Guards;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Entities;

/// <summary>
/// The TMS-side profile of an authenticated user who edits translations (ADR-0004). It is the small
/// counterpart to KittySaver's <c>Person</c> and holds the name the editor shows
/// (<see cref="DisplayName"/> and an optional <see cref="Email"/>), keyed by the
/// <see cref="IdentityId"/> that the AuthSystem owns.
/// The profile is created on the user's first authenticated request, and creating it twice is safe
/// (ADR-0004, amended 2026-06-24). So a user who registered and logged in has a profile before any
/// write. It is refreshed from the current claims whenever they change, so a renamed account catches
/// up on its own and needs no separate sync.
/// </summary>
public sealed class Translator : AggregateRoot<TranslatorId>
{
    public IdentityId IdentityId { get; }
    public DisplayName DisplayName { get; private set; }
    public Email? Email { get; private set; }
    public DateTimeOffset ProvisionedAt { get; }

    /// <summary>
    /// Copies the latest claims onto the profile, so a renamed account catches up. A <c>null</c>
    /// <paramref name="email"/> clears an address we knew before.
    /// </summary>
    public void RefreshProfile(DisplayName displayName, Email? email)
    {
        ArgumentNullException.ThrowIfNull(displayName);

        DisplayName = displayName;
        Email = email;
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
