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
    /// Currently only the Admin role can bypass every kind of privileges validation.
    /// </summary>
    bool HasOnlyRegularUserPrivileges();
}
