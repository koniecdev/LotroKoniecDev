using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;

namespace LotroKoniecDev.TranslationSystem.API.Auth.CurrentUserAccessing;

internal interface ICurrentUserAccessor
{
    ValueMaybe<IdentityId> MaybeIdentityId { get; }
    string? Email { get; }
    string? Username { get; }
    IEnumerable<string> Roles { get; }
    bool IsAuthenticated { get; }
    bool IsInRole(string role);

    /// <summary>
    /// A plain translator's saves spend the daily save budget; the trusted roles are free of it
    /// (ADR-0072 §1, amended by #1488).
    /// </summary>
    bool HasDailySaveBudget();
}
