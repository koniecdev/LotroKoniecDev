namespace LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;

/// <summary>
/// The few <c>data-testid</c> hooks added to the Auth Razor Pages where an element has no stable
/// accessible name to target — the two consent checkboxes and the post-action state panels. Inputs,
/// buttons and nav links carry no test-id: they are reached by role/label per Playwright's locator
/// guidance (<see cref="FieldLabels"/> / <see cref="Buttons"/> / <see cref="Links"/>).
/// </summary>
internal static class TestIds
{
    public const string RegisterAcceptPrivacy = "register-accept-privacy";
    public const string RegisterAcceptDataProcessing = "register-accept-data-processing";
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
