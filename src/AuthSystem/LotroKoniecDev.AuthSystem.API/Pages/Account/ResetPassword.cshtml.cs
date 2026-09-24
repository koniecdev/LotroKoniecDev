using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LotroKoniecDev.AuthSystem.API.Services.ResponseTiming;
using LotroKoniecDev.AuthSystem.API.Services.Sessions;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

namespace LotroKoniecDev.AuthSystem.API.Pages.Account;

internal sealed partial class ResetPasswordModel : PageModel
{
    private const string InvalidLinkMessage = "Link do resetu hasła jest nieprawidłowy lub wygasł.";

    /// <summary>
    /// A hash computed up front, so every path verifies exactly one hash.
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
        // returns before it. So every answer waits for the floor (ADR-0059).
        await _responseTimeFloor.HoldAsync(ResponseTimeFloors.AccountLookup, ResetAsync);

        return Page();
    }

    private async Task ResetAsync()
    {
        ApplicationUser? user = await _userManager.FindByEmailAsync(Email);

        // Every path pays the same PBKDF2 cost, the second layer under the floor (ADR-0059 §5).
        _ = _userManager.PasswordHasher.VerifyHashedPassword(
            new ApplicationUser(), DummyPasswordHash, "DummyP@ssw0rd!");

        if (user is null)
        {
            TokenInvalid = true;
            ErrorMessage = InvalidLinkMessage;
            return;
        }

        // While a GDPR deletion is scheduled, only the cancel link in the e-mail can bring the account
        // back. We show the general invalid-token message, so nobody can learn the state of an
        // account.
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

            ErrorMessage = string.Join(" ", result.Errors.Select(e => e.Description));
            return;
        }

        await _userManager.UpdateSecurityStampAsync(user);
        await _sessionRevoker.RevokeAllAsync(user.Id.ToString(), HttpContext.RequestAborted);

        LogPasswordResetCompleted(_logger, user.Id);

        IsCompleted = true;
    }

    [LoggerMessage(EventId = EventIds.PasswordResetCompletedViaUi, Level = LogLevel.Information, Message = "Password reset completed via UI for user {UserId}. Tokens and authorizations revoked.")]
    private static partial void LogPasswordResetCompleted(ILogger logger, Guid userId);

    [LoggerMessage(EventId = EventIds.ResetPasswordDeletionScheduled, Level = LogLevel.Warning, Message = "Password reset blocked for user {UserId}: account deletion is scheduled")]
    private static partial void LogPasswordResetBlockedDeletionScheduled(ILogger logger, Guid userId);
}
