using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Services.ResponseTiming;
using LotroKoniecDev.AuthSystem.API.Services.Sessions;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.Identity;
using LotroKoniecDev.SharedKernel.Constants;

namespace LotroKoniecDev.AuthSystem.API.Pages.Account;

internal sealed partial class ResetPasswordModel : PageModel
{
    private const string InvalidLinkMessage = "Link do resetu hasła jest nieprawidłowy lub wygasł.";

    private const string BreachedPasswordMessage = "To hasło pojawiło się w wyciekach danych, wybierz inne.";

    /// <summary>
    /// Every password rule has its own sentence, so only a failed save or another rule gets here. The form
    /// stays, and a second try either works or shows the link is used up.
    /// </summary>
    private const string GeneralRefusalMessage = "Spróbuj jeszcze raz. Jeśli błąd się powtórzy, napisz do nas.";

    /// <summary>
    /// So every path verifies exactly one hash.
    /// </summary>
    private static readonly string DummyPasswordHash =
        new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), "DummyP@ssw0rd!");

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IUserSessionRevoker _sessionRevoker;
    private readonly IResponseTimeFloor _responseTimeFloor;
    private readonly ILogger<ResetPasswordModel> _logger;

    public ResetPasswordModel(
        UserManager<ApplicationUser> userManager,
        IUserSessionRevoker sessionRevoker,
        IResponseTimeFloor responseTimeFloor,
        ILogger<ResetPasswordModel> logger)
    {
        _userManager = userManager;
        _sessionRevoker = sessionRevoker;
        _responseTimeFloor = responseTimeFloor;
        _logger = logger;
    }

    [BindProperty]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    public string Token { get; set; } = string.Empty;

    [BindProperty]
    public string NewPassword { get; set; } = string.Empty;

    [BindProperty]
    public string ConfirmPassword { get; set; } = string.Empty;

    public bool IsCompleted { get; set; }

    public bool TokenInvalid { get; set; }

    public string? ErrorMessage { get; set; }

    public void OnGet(string? email = null, string? token = null)
    {
        Email = email ?? string.Empty;
        Token = token ?? string.Empty;

        // Back from the done view loads this link again, and its button would send the used link (#941).
        // The answer rests on this browser's own cookie, so it reveals nothing and needs no time floor.
        IsCompleted = UsedLinkCookie.PasswordReset.WasUsedHere(Request, Token);
    }

    /// <summary>
    /// A successful POST redirects here, so a reload never sends the used link again (#886). Anybody can
    /// open this URL, so it prints nothing about any account.
    /// </summary>
    public void OnGetDone()
    {
        IsCompleted = true;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Token) ||
            string.IsNullOrWhiteSpace(NewPassword))
        {
            ErrorMessage = "Wszystkie pola są wymagane.";
            return Page();
        }

        if (NewPassword != ConfirmPassword)
        {
            ErrorMessage = "Hasła nie są identyczne.";
            return Page();
        }

        // The branches differ in cost: a wrong token fails at a cheap check, and a scheduled deletion
        // returns before it (ADR-0059).
        await _responseTimeFloor.HoldAsync(ResponseTimeFloors.AccountLookup, ResetAsync);

        if (!IsCompleted)
        {
            return Page();
        }

        // The POST never reads this cookie: a second send carries a password typed again, maybe a
        // different one, and "Twoje nowe hasło jest aktywne" would be false (ADR-0063).
        UsedLinkCookie.PasswordReset.Remember(HttpContext, Token);
        return RedirectToPage("/Account/ResetPassword", "Done");
    }

    private async Task ResetAsync()
    {
        ApplicationUser? user = await _userManager.FindByEmailAsync(Email);

        // On every path, the second layer under the floor (ADR-0059 §5).
        _ = _userManager.PasswordHasher.VerifyHashedPassword(
            new ApplicationUser(), DummyPasswordHash, "DummyP@ssw0rd!");

        if (user is null)
        {
            TokenInvalid = true;
            ErrorMessage = InvalidLinkMessage;
            return;
        }

        // During a scheduled deletion only the cancel link brings the account back. The general message
        // hides the account's state.
        if (user.DeletionScheduledAt is not null)
        {
            LogPasswordResetBlockedDeletionScheduled(_logger, user.Id);
            TokenInvalid = true;
            ErrorMessage = InvalidLinkMessage;
            return;
        }

        IdentityResult result = await _userManager.ResetPasswordAsync(user, Token, NewPassword);

        if (!result.Succeeded)
        {
            if (result.Errors.Any(e => e.Code is "InvalidToken"))
            {
                TokenInvalid = true;
                ErrorMessage = InvalidLinkMessage;
                return;
            }

            // The link is still good, so the form stays and takes another password (ADR-0066).
            if (result.IsBreachedPassword)
            {
                ErrorMessage = BreachedPasswordMessage;
                return;
            }

            // The user never sees Identity's text, so a code the page cannot name is kept in the log.
            string[] unnamedCodes = result.Errors
                .Select(error => error.Code)
                .Where(code => PolishFor(code) is null)
                .ToArray();
            if (unnamedCodes.Length > 0)
            {
                LogPasswordResetFailed(_logger, user.Id, string.Join(", ", unnamedCodes));
            }

            ErrorMessage = string.Join(
                " ",
                result.Errors.Select(error => PolishFor(error.Code) ?? GeneralRefusalMessage).Distinct());
            return;
        }

        // ResetPasswordAsync gave the account a new security stamp in its one save (#874).
        await _sessionRevoker.RevokeAllAsync(user.Id.ToString());

        LogPasswordResetCompleted(_logger, user.Id);

        IsCompleted = true;
    }

    /// <summary>
    /// Identity's errors are English, so the page names each broken rule in Polish by its code (#1076).
    /// </summary>
    private static string? PolishFor(string code) =>
        code switch
        {
            nameof(IdentityErrorDescriber.PasswordTooShort) =>
                $"Hasło musi mieć co najmniej {PasswordConstants.MinLength} znaków.",
            PasswordMaxLengthValidator.ErrorCode =>
                $"Hasło może mieć co najwyżej {PasswordConstants.MaxLength} znaków.",
            nameof(IdentityErrorDescriber.PasswordRequiresLower) => "Hasło musi zawierać małą literę.",
            nameof(IdentityErrorDescriber.PasswordRequiresUpper) => "Hasło musi zawierać wielką literę.",
            nameof(IdentityErrorDescriber.PasswordRequiresDigit) => "Hasło musi zawierać cyfrę.",
            nameof(IdentityErrorDescriber.PasswordRequiresNonAlphanumeric) => "Hasło musi zawierać znak specjalny.",
            _ => null
        };

    [LoggerMessage(EventId = EventIds.PasswordResetCompletedViaUi, Level = LogLevel.Information, Message = "Password reset completed via UI for user {UserId}. Tokens and authorizations revoked.")]
    private static partial void LogPasswordResetCompleted(ILogger logger, Guid userId);

    [LoggerMessage(EventId = EventIds.ResetPasswordFailed, Level = LogLevel.Warning, Message = "Password reset failed via UI for user {UserId}. Error codes: {ErrorCodes}")]
    private static partial void LogPasswordResetFailed(ILogger logger, Guid userId, string errorCodes);

    [LoggerMessage(EventId = EventIds.ResetPasswordDeletionScheduled, Level = LogLevel.Warning, Message = "Password reset blocked for user {UserId}: account deletion is scheduled")]
    private static partial void LogPasswordResetBlockedDeletionScheduled(ILogger logger, Guid userId);
}
