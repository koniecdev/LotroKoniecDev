namespace LotroKoniecDev.SharedKernel.Authorization;

public static class AuthConstants
{
    public static class Roles
    {
        public const string Admin = "Admin";
        public const string Translator = "Translator";
    }

    public static class Scopes
    {
        public const string Api = "api";
        public const string Service = "service";
    }

    public static class ClientIds
    {
        public const string Web = "lotrokoniecdev-web";
        public const string Api = "lotrokoniecdev-api";
    }

    /// <summary>
    /// The translation API's audience is its client id, <see cref="ClientIds.Api"/>.
    /// </summary>
    public static class Audiences
    {
        public const string AuthApi = "lotrokoniecdev-auth-api";
    }
}
