using System.Security.Claims;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;

namespace LotroKoniecDev.TranslationSystem.API.Auth.CurrentUserAccessing;

internal sealed class CurrentUserAccessor : ICurrentUserAccessor
{
    // OpenIddict standard claim types — MapInboundClaims is disabled, so they arrive as-is.
    private const string SubjectClaimType = "sub";
    private const string EmailClaimType = "email";
    private const string NameClaimType = "name";
    private const string RoleClaimType = "role";

    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentUserAccessor(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    private ClaimsPrincipal? User => _httpContextAccessor.HttpContext?.User;

    public bool IsAuthenticated => User?.Identity?.IsAuthenticated ?? false;

    public ValueMaybe<IdentityId> MaybeIdentityId
    {
        get
        {
            string? subject = User?.FindFirstValue(SubjectClaimType);
            IdentityId? result = Guid.TryParse(subject, out Guid userId) ? IdentityId.FromValue(userId) : null;
            return ValueMaybe<IdentityId>.From(result);
        }
    }

    public string? Email => User?.FindFirstValue(EmailClaimType);

    public string? Username => User?.FindFirstValue(NameClaimType);

    public IEnumerable<string> Roles => User?
        .FindAll(RoleClaimType)
        .Select(claim => claim.Value) ?? [];

    public bool IsInRole(string role) => User?.IsInRole(role) ?? false;

    public bool HasOnlyRegularUserPrivileges()
    {
        if (User is null)
        {
            return true;
        }

        bool isUserAdmin = User.IsInRole(AuthConstants.Roles.Admin);

        return !isUserAdmin;
    }
}
