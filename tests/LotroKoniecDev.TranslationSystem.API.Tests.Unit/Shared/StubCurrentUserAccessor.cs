using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.API.Auth.CurrentUserAccessing;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Shared;

/// <summary>
/// NSubstitute cannot proxy the internal <see cref="ICurrentUserAccessor"/>.
/// </summary>
internal sealed class StubCurrentUserAccessor : ICurrentUserAccessor
{
    private readonly bool _isAdmin;

    public StubCurrentUserAccessor(
        ValueMaybe<IdentityId> identityId,
        string? username = null,
        string? email = null,
        bool isAdmin = false)
    {
        MaybeIdentityId = identityId;
        Username = username;
        Email = email;
        _isAdmin = isAdmin;
    }

    public ValueMaybe<IdentityId> MaybeIdentityId { get; }
    public string? Email { get; }
    public string? Username { get; }
    public IEnumerable<string> Roles => [];
    public bool IsAuthenticated => MaybeIdentityId.HasValue;
    public bool IsInRole(string role) => _isAdmin && role == AuthConstants.Roles.Admin;
    public bool HasOnlyRegularUserPrivileges() => !_isAdmin;
}
