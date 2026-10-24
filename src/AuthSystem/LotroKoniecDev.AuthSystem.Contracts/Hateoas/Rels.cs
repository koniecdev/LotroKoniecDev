namespace LotroKoniecDev.AuthSystem.Contracts.Hateoas;

public static class Rels
{
    public const string Self = "self";

    // Account aggregate
    public const string ChangePassword = "change-password";
    public const string ChangeEmail = "change-email";
    public const string DeleteAccount = "delete-account";
    public const string CancelDeletion = "cancel-deletion";
    public const string ResendEmailConfirmation = "resend-email-confirmation";

    // Discovery
    public const string Register = "register";
    public const string ForgotPassword = "forgot-password";

    /// <summary>
    /// The auth root offers this rel only to logged-in callers, and the frontend's <c>DiscoveryCache</c>
    /// uses it as the sign-in marker: when it is missing for a logged-in user, the session is signed out.
    /// Renaming it, or not sending it to some logged-in caller, signs every user out on the next page
    /// load. Change the frontend guard in the same commit.
    /// </summary>
    public const string ExportAccountData = "export-account-data";

    /// <summary>
    /// The password-gated GDPR export (ADR-0052). It is a separate rel because
    /// <see cref="ExportAccountData"/> is the sign-in marker and has to stay a GET that needs only a token.
    /// It is advertised on the account resource, not in the cached discovery document, so a client
    /// always reads it fresh.
    /// </summary>
    public const string DownloadAccountData = "download-account-data";
}
