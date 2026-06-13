namespace LotroKoniecDev.TranslationSystem.API.Auth;

internal static class AuthorizationPolicies
{
    public const string RequireAuthenticatedUser = nameof(RequireAuthenticatedUser);
    public const string RequireAdminRole = nameof(RequireAdminRole);
    public const string RequireTranslatorRole = nameof(RequireTranslatorRole);
    public const string ApiScope = nameof(ApiScope);
    public const string RequireServiceScope = nameof(RequireServiceScope);
}
