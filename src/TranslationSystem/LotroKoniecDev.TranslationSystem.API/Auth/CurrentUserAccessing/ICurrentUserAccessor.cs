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
    /// Today only the Admin role can skip every permission check.
    /// </summary>
    bool HasOnlyRegularUserPrivileges();
}
