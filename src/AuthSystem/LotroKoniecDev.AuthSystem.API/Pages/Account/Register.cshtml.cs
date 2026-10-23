using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.API.Features.Auth;
using LotroKoniecDev.AuthSystem.API.Settings;
using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Constants;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Pages.Account;

internal sealed partial class RegisterModel : PageModel
{
    /// <summary>
    /// The frontend's terms-of-service route: a rename there has to be repeated here.
    /// </summary>
    private const string TermsOfServicePath = "/regulamin";

    private readonly ICommandHandler<RegisterUser.Command, Result> _registerUserHandler;
    private readonly IOptions<OpenIddictSettings> _openIddictSettings;

    public RegisterModel(
        ICommandHandler<RegisterUser.Command, Result> registerUserHandler,
        IOptions<OpenIddictSettings> openIddictSettings)
    {
        _registerUserHandler = registerUserHandler;
        _openIddictSettings = openIddictSettings;
    }

    [BindProperty]
    public string Username { get; set; } = string.Empty;

    [BindProperty]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    public string ConfirmPassword { get; set; } = string.Empty;

    [BindProperty]
    public bool AcceptedPrivacyPolicy { get; set; }

    [BindProperty]
    public bool AcceptedTermsOfService { get; set; }

    public bool IsRegistered { get; set; }

    /// <summary>
    /// Null when the web client is not configured, and the page then shows the consent text without a link.
    /// </summary>
    public string? TermsOfServiceUrl =>
        FrontendUrl.For(_openIddictSettings.Value.WebClient, TermsOfServicePath);

    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Usually the <c>/connect/authorize</c> URL the user came from, passed on to the login link shown
    /// after registration. Printed only after <see cref="LocalReturnUrl.Sanitize"/> accepts it.
    /// </summary>
    public string? ReturnUrl { get; set; }

    public void OnGet(string? returnUrl = null)
    {
        ReturnUrl = LocalReturnUrl.Sanitize(returnUrl);
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl, CancellationToken cancellationToken)
    {
        ReturnUrl = LocalReturnUrl.Sanitize(returnUrl);

        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Email) ||
            string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Wszystkie pola są wymagane.";
            return Page();
        }

        // A copy of the validator's rule, only for the message: otherwise a bad character would get the
        // general message, which talks about the password.
        if (!UsernameRegex().IsMatch(Username.Trim()))
        {
            ErrorMessage = "Nazwa użytkownika może zawierać tylko litery i cyfry, bez spacji.";
            return Page();
        }

        if (Password != ConfirmPassword)
        {
            ErrorMessage = "Hasła nie są identyczne.";
            return Page();
        }

        if (!AcceptedPrivacyPolicy)
        {
            ErrorMessage = "Musisz zaakceptować politykę prywatności, aby założyć konto.";
            return Page();
        }

        if (!AcceptedTermsOfService)
        {
            ErrorMessage = "Musisz zaakceptować regulamin serwisu, aby założyć konto.";
            return Page();
        }

        RegisterUser.Command command = new(
            Username.Trim(),
            Email.Trim(),
            Password,
            AcceptedPrivacyPolicy,
            AcceptedTermsOfService);

        Result result = await _registerUserHandler.Handle(command, cancellationToken);

        if (result.IsFailure)
        {
            ErrorMessage = MapErrorToMessage(result.Error);
            return Page();
        }

        // Also for an address that already has an account: the page must not tell the two apart (#860).
        IsRegistered = true;
        return Page();
    }

    /// <summary>
    /// A taken e-mail address is not an error here: it gets the same answer as a free one (#860).
    /// </summary>
    private static string MapErrorToMessage(Error error)
    {
        return error.Code switch
        {
            "Auth.UserAlreadyExistsByUsername" =>
                "Ta nazwa użytkownika jest już zajęta. Wybierz inną.",
            "Auth.RegistrationMailboxThrottled" =>
                "Na tę skrzynkę pocztową było ostatnio zbyt wiele prób rejestracji. Odczekaj kwadrans i spróbuj ponownie.",
            "Auth.PasswordFoundInBreaches" =>
                "To hasło pojawiło się w wyciekach danych, wybierz inne.",
            _ =>
                "Nie udało się założyć konta. Upewnij się, że hasło spełnia wymagania, i spróbuj ponownie."
        };
    }

    [GeneratedRegex(UsernameConstants.RegexPattern)]
    private static partial Regex UsernameRegex();
}
