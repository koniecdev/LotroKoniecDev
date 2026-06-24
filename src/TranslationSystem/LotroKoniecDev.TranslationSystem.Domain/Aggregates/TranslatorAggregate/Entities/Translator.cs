using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Guards;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Entities;

/// <summary>
/// The TMS-local projection of an authenticated user who edits translations (ADR-0004): the lean
/// counterpart to KittySaver's <c>Person</c>. Holds the human-readable identity the editor renders
/// (<see cref="DisplayName"/>, optional <see cref="Email"/>) keyed by the cross-context
/// <see cref="IdentityId"/> (the AuthSystem user id). Provisioned lazily and idempotently on the
/// caller's first authenticated request (ADR-0004 amendment 2026-06-24), so a registered and logged-in
/// user has a profile before any write; the profile refreshes from
/// the current claims whenever they change, so a renamed account converges without a separate sync.
/// </summary>
public sealed class Translator : AggregateRoot<TranslatorId>
{
    public IdentityId IdentityId { get; }
    public DisplayName DisplayName { get; private set; }
    public Email? Email { get; private set; }
    public DateTimeOffset ProvisionedAt { get; }

    /// <summary>
    /// Re-applies the latest claims on an authenticated touch: refreshes the display name and email so
    /// a renamed account converges. A <c>null</c> <paramref name="email"/> clears a previously known
    /// address.
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
