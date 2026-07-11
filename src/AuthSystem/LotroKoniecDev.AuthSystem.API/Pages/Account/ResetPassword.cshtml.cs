using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LotroKoniecDev.AuthSystem.API.Services.Sessions;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

namespace LotroKoniecDev.AuthSystem.API.Pages.Account;

internal sealed partial class ResetPasswordModel : PageModel
{
    /// <summary>
    /// Pre-computed hash for timing-equalization when user is not found.
    /// </summary>
    private static readonly string DummyPasswordHash =
        new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), "DummyP@ssw0rd!");

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IUserSessionRevoker _sessionRevoker;
    private readonly ILogger<ResetPasswordModel> _logger;

    public ResetPasswordModel(
        UserManager<ApplicationUser> userManager,
        IUserSessionRevoker sessionRevoker,
        ILogger<ResetPasswordModel> logger)
    {
        _userManager = userManager;
        _sessionRevoker = sessionRevoker;
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

        ApplicationUser? user = await _userManager.FindByEmailAsync(Email);

        if (user is null)
        {
            // Perform dummy work to prevent timing-based user enumeration
            _ = new PasswordHasher<ApplicationUser>()
                .VerifyHashedPassword(new ApplicationUser(), DummyPasswordHash, "DummyP@ssw0rd!");

            TokenInvalid = true;
            ErrorMessage = "Link do resetu hasła jest nieprawidłowy lub wygasł.";
            return Page();
        }

        // While GDPR deletion is scheduled, only the emailed cancel-deletion link may
        // restore the account. The generic invalid-token message prevents state probing.
        if (user.DeletionScheduledAt is not null)
        {
            LogPasswordResetBlockedDeletionScheduled(_logger, user.Id);
            TokenInvalid = true;
            ErrorMessage = "Link do resetu hasła jest nieprawidłowy lub wygasł.";
            return Page();
        }

        IdentityResult result = await _userManager.ResetPasswordAsync(user, Token, NewPassword);

        if (!result.Succeeded)
        {
            if (result.Errors.Any(e => e.Code is "InvalidToken"))
            {
                TokenInvalid = true;
                ErrorMessage = "Link do resetu hasła jest nieprawidłowy lub wygasł.";
                return Page();
            }

            ErrorMessage = string.Join(" ", result.Errors.Select(e => e.Description));
            return Page();
        }

        await _userManager.UpdateSecurityStampAsync(user);
        await _sessionRevoker.RevokeAllAsync(user.Id.ToString(), HttpContext.RequestAborted);

        LogPasswordResetCompleted(_logger, user.Id);

        IsCompleted = true;
        return Page();
    }

    [LoggerMessage(EventId = EventIds.PasswordResetCompletedViaUi, Level = LogLevel.Information, Message = "Password reset completed via UI for user {UserId}. Tokens and authorizations revoked.")]
    private static partial void LogPasswordResetCompleted(ILogger logger, Guid userId);

    [LoggerMessage(EventId = EventIds.ResetPasswordDeletionScheduled, Level = LogLevel.Warning, Message = "Password reset blocked for user {UserId}: account deletion is scheduled")]
    private static partial void LogPasswordResetBlockedDeletionScheduled(ILogger logger, Guid userId);
}
