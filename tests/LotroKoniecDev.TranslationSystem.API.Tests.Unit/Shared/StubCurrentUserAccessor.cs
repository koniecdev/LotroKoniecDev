using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.API.Auth.CurrentUserAccessing;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Shared;

/// <summary>
/// NSubstitute cannot proxy the internal <see cref="ICurrentUserAccessor"/>.
/// </summary>
internal sealed class StubCurrentUserAccessor : ICurrentUserAccessor
{
    public StubCurrentUserAccessor(
        ValueMaybe<IdentityId> identityId,
        string? username = null,
        string? email = null)
    {
        MaybeIdentityId = identityId;
        Username = username;
        Email = email;
    }

    public ValueMaybe<IdentityId> MaybeIdentityId { get; }
    public string? Email { get; }
    public string? Username { get; }
    public IEnumerable<string> Roles => [];
    public bool IsAuthenticated => MaybeIdentityId.HasValue;
    public bool IsInRole(string role) => false;
    public bool HasOnlyRegularUserPrivileges() => true;
}
