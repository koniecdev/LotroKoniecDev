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
    /// The caller's own account export. <b>Load-bearing beyond its endpoint:</b> the auth root advertises
    /// it only to authenticated callers, so the frontend's <c>DiscoveryCache</c> treats its absence under
    /// an authenticated cache key as proof the bearer never reached the API, and force-signs the session
    /// out. Renaming this rel — or stopping its emission to any authenticated caller — signs every
    /// logged-in user out on their next page load; change the frontend guard in the same commit.
    /// </summary>
    public const string ExportAccountData = "export-account-data";
}
