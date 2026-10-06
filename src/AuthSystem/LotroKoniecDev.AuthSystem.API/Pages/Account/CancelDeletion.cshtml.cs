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
    private readonly UsedLinkNextStepCookie _nextStepCookie;
    private readonly ILogger<CancelDeletionModel> _logger;

    public CancelDeletionModel(
        ICommandHandler<CancelAccountDeletion.Command, Result<CancelAccountDeletion.CancelledDeletion>> handler,
        UsedLinkNextStepCookie nextStepCookie,
        ILogger<CancelDeletionModel> logger)
    {
        _handler = handler;
        _nextStepCookie = nextStepCookie;
        _logger = logger;
    }

    [BindProperty]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    public string Token { get; set; } = string.Empty;

    public string? ErrorMessage { get; set; }

    public IActionResult OnGet(string? email = null, string? token = null)
    {
        Email = email ?? string.Empty;
        Token = token ?? string.Empty;

        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Token))
        {
            ErrorMessage = "Link anulujący usunięcie konta jest nieprawidłowy.";
            return Page();
        }

        // Back from the password form loads this used link again. Its button would call it dead (#941).
        return RedirectToNextStepIfUsedHere() ?? Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Token))
        {
            ErrorMessage = "Link anulujący usunięcie konta jest nieprawidłowy.";
            return Page();
        }

        // A form the browser brought back from its cache can still send a link this browser already used.
        // The link asks for no input, so this answer is the same as the first one (#941).
        if (RedirectToNextStepIfUsedHere() is { } redirect)
        {
            return redirect;
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

        PasswordResetStep nextStep = new(Email, commandResult.Value.PasswordResetToken);
        _nextStepCookie.Remember(HttpContext, UsedLinkFlow.DeletionCancel, Token, nextStep);
        return RedirectToPasswordReset(nextStep);
    }

    private IActionResult? RedirectToNextStepIfUsedHere() =>
        _nextStepCookie.NextStepFor(Request, UsedLinkFlow.DeletionCancel, Token) is { } nextStep
            ? RedirectToPasswordReset(nextStep)
            : null;

    private RedirectToPageResult RedirectToPasswordReset(PasswordResetStep nextStep) =>
        RedirectToPage("/Account/ResetPassword", new { email = nextStep.Email, token = nextStep.Token });

    [LoggerMessage(EventId = EventIds.DeletionCancelledViaUi, Level = LogLevel.Information, Message = "Account deletion cancelled via UI for {MaskedEmail}. Redirecting to the forced password reset.")]
    private static partial void LogDeletionCancelledViaUi(ILogger logger, string maskedEmail);
}
