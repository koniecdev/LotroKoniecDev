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
using LotroKoniecDev.SharedKernel.StronglyTypedIds;

namespace LotroKoniecDev.AuthSystem.API.Pages.Account;

internal sealed partial class RegisterModel : PageModel
{
    private readonly ICommandHandler<RegisterUser.Command, Result<IdentityId>> _registerUserHandler;
    private readonly IOptions<OpenIddictSettings> _openIddictSettings;
    private readonly ILogger<RegisterModel> _logger;

    public RegisterModel(
        ICommandHandler<RegisterUser.Command, Result<IdentityId>> registerUserHandler,
        IOptions<OpenIddictSettings> openIddictSettings,
        ILogger<RegisterModel> logger)
    {
        _registerUserHandler = registerUserHandler;
        _openIddictSettings = openIddictSettings;
        _logger = logger;
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
    public bool AcceptedDataProcessingConsent { get; set; }

    [BindProperty]
    public bool AcceptedTermsOfService { get; set; }

    public bool IsRegistered { get; set; }

    /// <summary>
    /// Absolute URL of the terms-of-service page on the frontend, derived from the web client's
    /// first post-logout redirect URI (the app root) so no separate frontend-origin setting is
    /// needed. Null when the client is not configured (e.g. a bare test host) — the register page
    /// then renders the consent label without a link.
    /// </summary>
    public string? TermsOfServiceUrl =>
        _openIddictSettings.Value.WebClient.PostLogoutRedirectUris is [string appRoot, ..]
        && Uri.TryCreate(appRoot, UriKind.Absolute, out Uri? appRootUri)
            ? new Uri(appRootUri, "/regulamin").ToString()
            : null;

    public string? ErrorMessage { get; set; }

    /// <summary>
    /// The local OIDC continuation captured from the login flow (e.g. the <c>/connect/authorize</c>
    /// URL the user was bounced from). Carried verbatim into the post-registration login link so a
    /// registration detour resumes the interrupted authorization once the account is confirmed and
    /// signed in. Reflected into the page only after passing <see cref="LocalReturnUrl.Sanitize"/> to
    /// block open redirects.
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

        // UX-only mirror of the authoritative UsernameConstants rule in RegisterUser.CommandValidator —
        // without it a charset failure would fall through to the generic (password-hinting) message.
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

        if (!AcceptedDataProcessingConsent)
        {
            ErrorMessage = "Musisz wyrazić zgodę na przetwarzanie danych, aby założyć konto.";
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
            AcceptedDataProcessingConsent,
            AcceptedTermsOfService);

        Result<IdentityId> result = await _registerUserHandler.Handle(command, cancellationToken);

        if (result.IsFailure)
        {
            ErrorMessage = MapErrorToMessage(result.Error);
            return Page();
        }

        LogRegisteredViaUi(_logger, result.Value);

        IsRegistered = true;
        return Page();
    }

    /// <summary>
    /// Maps the authoritative handler error onto a friendly Polish message. The required-field,
    /// password-match and consent cases are caught above, so the remaining handler failures are the
    /// duplicate-identity conflicts and the password-complexity validation backstop.
    /// </summary>
    private static string MapErrorToMessage(Error error)
    {
        return error.Code switch
        {
            "Auth.UserAlreadyExistsByEmail" =>
                "Konto z tym adresem e-mail już istnieje. Zaloguj się lub zresetuj hasło.",
            "Auth.UserAlreadyExistsByUsername" =>
                "Ta nazwa użytkownika jest już zajęta. Wybierz inną.",
            _ =>
                "Nie udało się założyć konta. Upewnij się, że hasło spełnia wymagania, i spróbuj ponownie."
        };
    }

    [LoggerMessage(EventId = EventIds.RegisterCompletedViaUi, Level = LogLevel.Information, Message = "User {UserId} registered via UI")]
    private static partial void LogRegisteredViaUi(ILogger logger, IdentityId userId);

    [GeneratedRegex(UsernameConstants.RegexPattern)]
    private static partial Regex UsernameRegex();
}
