using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LotroKoniecDev.AuthSystem.API.Features.Auth;
using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;

namespace LotroKoniecDev.AuthSystem.API.Pages.Account;

internal sealed partial class RegisterModel : PageModel
{
    private readonly ICommandHandler<RegisterUser.Command, Result<IdentityId>> _registerUserHandler;
    private readonly ILogger<RegisterModel> _logger;

    public RegisterModel(
        ICommandHandler<RegisterUser.Command, Result<IdentityId>> registerUserHandler,
        ILogger<RegisterModel> logger)
    {
        _registerUserHandler = registerUserHandler;
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

    public bool IsRegistered { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>
    /// The local OIDC continuation captured from the login flow (e.g. the <c>/connect/authorize</c>
    /// URL the user was bounced from). Carried verbatim into the post-registration login link so a
    /// registration detour resumes the interrupted authorization once the account is confirmed and
    /// signed in. Reflected into the page only after passing <see cref="SanitizeReturnUrl"/> to block
    /// open redirects.
    /// </summary>
    public string? ReturnUrl { get; set; }

    public void OnGet(string? returnUrl = null)
    {
        ReturnUrl = SanitizeReturnUrl(returnUrl);
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl, CancellationToken cancellationToken)
    {
        ReturnUrl = SanitizeReturnUrl(returnUrl);

        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Email) ||
            string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Wszystkie pola są wymagane.";
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

        RegisterUser.Command command = new(
            Username.Trim(),
            Email.Trim(),
            Password,
            AcceptedPrivacyPolicy,
            AcceptedDataProcessingConsent);

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
    /// Keeps only a safe, same-site <paramref name="returnUrl"/>: a non-local value (an absolute or
    /// protocol-relative URL) is dropped so it can never be reflected into a link and turned into an
    /// open redirect. Mirrors the login page's <see cref="IUrlHelper.IsLocalUrl"/> guard.
    /// </summary>
    private string? SanitizeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : null;

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
}
