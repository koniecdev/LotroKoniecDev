using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Features.Auth;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Pages.Account;

/// <summary>
/// The page the "to nie ja" link in the old mailbox leads to (ADR-0048). A GET only shows a
/// confirmation form. That is not decoration: this link lands in a live inbox, corporate mail
/// security opens every URL it sees, and a GET that reverted would undo every legitimate e-mail
/// change and clear the password seconds after the notice arrived.
/// </summary>
[EnableRateLimiting("auth-endpoint-limit")]
internal sealed partial class RevertEmailChangeModel : PageModel
{
    private readonly ICommandHandler<RevertEmailChange.Command, Result<RevertEmailChange.RevertedEmailChange>> _handler;
    private readonly ILogger<RevertEmailChangeModel> _logger;

    public RevertEmailChangeModel(
        ICommandHandler<RevertEmailChange.Command, Result<RevertEmailChange.RevertedEmailChange>> handler,
        ILogger<RevertEmailChangeModel> logger)
    {
        _handler = handler;
        _logger = logger;
    }

    [BindProperty]
    public string UserId { get; set; } = string.Empty;

    /// <summary>The address the account is going back to.</summary>
    [BindProperty]
    public string From { get; set; } = string.Empty;

    /// <summary>The address the account was moved to, and where it has to still be for this to work.</summary>
    [BindProperty]
    public string To { get; set; } = string.Empty;

    [BindProperty]
    public string Token { get; set; } = string.Empty;

    public string? ErrorMessage { get; set; }

    public void OnGet(string? userId = null, string? from = null, string? to = null, string? token = null)
    {
        UserId = userId ?? string.Empty;
        From = from ?? string.Empty;
        To = to ?? string.Empty;
        Token = token ?? string.Empty;

        if (!HasEveryValue())
        {
            ErrorMessage = "Link cofający zmianę adresu jest nieprawidłowy.";
        }
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!HasEveryValue())
        {
            ErrorMessage = "Link cofający zmianę adresu jest nieprawidłowy.";
            return Page();
        }

        RevertEmailChange.Command command = new(
            UserId,
            From,
            To,
            Token,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            HttpContext.Request.Headers.UserAgent.ToString());

        Result<RevertEmailChange.RevertedEmailChange> commandResult =
            await _handler.Handle(command, HttpContext.RequestAborted);

        if (commandResult.IsFailure)
        {
            ErrorMessage = "Link cofający zmianę adresu jest nieprawidłowy lub wygasł.";
            return Page();
        }

        LogEmailChangeRevertedViaUi(_logger, From.MaskEmail());

        // The password is gone now, so the only way back into the account is the reset flow. Same
        // ending as cancelling a scheduled deletion.
        return RedirectToPage("/Account/ResetPassword", new
        {
            email = commandResult.Value.RestoredEmail,
            token = commandResult.Value.PasswordResetToken
        });
    }

    private bool HasEveryValue() =>
        !string.IsNullOrWhiteSpace(UserId)
        && !string.IsNullOrWhiteSpace(From)
        && !string.IsNullOrWhiteSpace(To)
        && !string.IsNullOrWhiteSpace(Token);

    [LoggerMessage(EventId = EventIds.EmailChangeRevertedViaUi, Level = LogLevel.Information, Message = "E-mail change reverted via UI for {MaskedEmail}. Redirecting to the forced password reset.")]
    private static partial void LogEmailChangeRevertedViaUi(ILogger logger, string maskedEmail);
}
