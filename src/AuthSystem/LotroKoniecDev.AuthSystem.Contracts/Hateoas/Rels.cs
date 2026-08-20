namespace LotroKoniecDev.AuthSystem.Contracts.Hateoas;

public static class Rels
{
    public const string Self = "self";

    // Account aggregate
    public const string ChangePassword = "change-password";
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
}
