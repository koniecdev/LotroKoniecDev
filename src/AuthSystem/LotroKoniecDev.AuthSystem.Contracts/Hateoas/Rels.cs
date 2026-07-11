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
    public const string ExportAccountData = "export-account-data";
}
