namespace LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;

/// <summary>
/// Only for elements with no stable accessible name. Everything else is found by role or label, as
/// Playwright recommends.
/// </summary>
internal static class TestIds
{
    public const string RegisterAcceptPrivacy = "register-accept-privacy";
    public const string RegisterAcceptTerms = "register-accept-terms";
    public const string RegisterSuccess = "register-success";
    public const string ConfirmEmailSuccess = "confirm-email-success";
}

/// <summary>Accessible names of the form fields (the <c>&lt;label&gt;</c> text), for <c>GetByLabel</c>.</summary>
internal static class FieldLabels
{
    public const string Username = "Nazwa użytkownika";
    public const string Email = "Adres e-mail";
    public const string Password = "Hasło";
    public const string ConfirmPassword = "Powtórz hasło";
}

/// <summary>Accessible names of the action buttons, for <c>GetByRole(AriaRole.Button)</c>.</summary>
internal static class Buttons
{
    public const string Register = "Załóż konto";
    public const string Login = "Zaloguj się";
    public const string Logout = "Wyloguj";
}

/// <summary>Accessible names of the nav links, for <c>GetByRole(AriaRole.Link)</c>.</summary>
internal static class Links
{
    public const string Login = "Zaloguj";
}
