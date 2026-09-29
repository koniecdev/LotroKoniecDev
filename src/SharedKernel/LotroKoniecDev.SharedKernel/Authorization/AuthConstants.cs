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
    /// The <c>typ</c> header values of a JWT access token (RFC 9068). OpenIddict writes the short form.
    /// </summary>
    public static class TokenTypes
    {
        public const string AccessToken = "at+jwt";
        public const string AccessTokenMediaType = "application/at+jwt";
    }
}
