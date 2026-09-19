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
    /// The caller's own account export. <b>This rel does more than name an endpoint.</b> The auth root
    /// offers it only to logged-in callers, so when the frontend's <c>DiscoveryCache</c> does not see it
    /// under an authenticated cache key, it concludes the token never reached the API and signs the
    /// session out. Renaming this rel, or no longer sending it to some logged-in caller, signs every
    /// logged-in user out on their next page load. Change the frontend guard in the same commit.
    /// </summary>
    public const string ExportAccountData = "export-account-data";

    /// <summary>
    /// The password-gated GDPR export (#690, ADR-0052): the same data as <see cref="ExportAccountData"/>,
    /// but handed over only to a caller who sends the current password with the POST. It is a separate
    /// rel on purpose — <see cref="ExportAccountData"/> has the sign-in duty described above and has to
    /// stay a GET that needs nothing but a token.
    /// </summary>
    public const string DownloadAccountData = "download-account-data";
}
