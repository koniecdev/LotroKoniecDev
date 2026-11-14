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
        /// <summary>
        /// The website's old public client. It stays for one release after the website moved to
        /// <see cref="Frontend"/>, then a follow-up removes it (ADR-0074).
        /// </summary>
        public const string Web = "lotrokoniecdev-web";

        /// <summary>
        /// The website, a confidential client that keeps PKCE (ADR-0074).
        /// </summary>
        public const string Frontend = "lotrokoniecdev-frontend";

        public const string Api = "lotrokoniecdev-api";
    }

    /// <summary>
    /// The translation API's audience is its client id, <see cref="ClientIds.Api"/>.
    /// </summary>
    public static class Audiences
    {
        public const string AuthApi = "lotrokoniecdev-auth-api";
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
