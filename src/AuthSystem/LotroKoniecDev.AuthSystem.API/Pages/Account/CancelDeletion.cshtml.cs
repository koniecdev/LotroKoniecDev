using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Features.Auth;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Pages.Account;

/// <summary>
/// The page the cancel-deletion link in the e-mail leads to, sent when a GDPR account deletion is
/// scheduled (ADR-0031). A GET only shows a confirmation form, so a mail scanner that opens the link
/// cancels nothing. The POST does the cancellation and sends the user straight into the password reset
/// flow.
/// </summary>
[EnableRateLimiting("auth-endpoint-limit")]
internal sealed partial class CancelDeletionModel : PageModel
{
    private readonly ICommandHandler<CancelAccountDeletion.Command, Result<CancelAccountDeletion.CancelledDeletion>> _handler;
    private readonly ILogger<CancelDeletionModel> _logger;

    public CancelDeletionModel(
        ICommandHandler<CancelAccountDeletion.Command, Result<CancelAccountDeletion.CancelledDeletion>> handler,
        ILogger<CancelDeletionModel> logger)
    {
        _handler = handler;
        _logger = logger;
    }

    [BindProperty]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    public string Token { get; set; } = string.Empty;

    public string? ErrorMessage { get; set; }

    public void OnGet(string? email = null, string? token = null)
    {
        Email = email ?? string.Empty;
        Token = token ?? string.Empty;

        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Token))
        {
            ErrorMessage = "Link anulujący usunięcie konta jest nieprawidłowy.";
        }
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Token))
        {
            ErrorMessage = "Link anulujący usunięcie konta jest nieprawidłowy.";
            return Page();
        }

        CancelAccountDeletion.Command command = new(
            Email,
            Token,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            HttpContext.Request.Headers.UserAgent.ToString());

        Result<CancelAccountDeletion.CancelledDeletion> commandResult =
            await _handler.Handle(command, HttpContext.RequestAborted);

        if (commandResult.IsFailure)
        {
            ErrorMessage = "Link anulujący usunięcie konta jest nieprawidłowy lub wygasł.";
            return Page();
        }

        string maskedEmail = Email.MaskEmail();
        LogDeletionCancelledViaUi(_logger, maskedEmail);

        return RedirectToPage("/Account/ResetPassword", new
        {
            email = Email,
            token = commandResult.Value.PasswordResetToken
        });
    }

    [LoggerMessage(EventId = EventIds.DeletionCancelledViaUi, Level = LogLevel.Information, Message = "Account deletion cancelled via UI for {MaskedEmail}. Redirecting to the forced password reset.")]
    private static partial void LogDeletionCancelledViaUi(ILogger logger, string maskedEmail);
}
