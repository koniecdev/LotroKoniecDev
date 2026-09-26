using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Features.Auth;
using LotroKoniecDev.SharedKernel.BuildingBlocks;
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

    public string? ErrorTitle { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>
    /// The link passed its token check and only the save failed, so the page shows the form again and
    /// one more click can finish the undo (#869).
    /// </summary>
    public bool CanRetry { get; set; }

    public void OnGet(string? userId = null, string? from = null, string? to = null, string? token = null)
    {
        UserId = userId ?? string.Empty;
        From = from ?? string.Empty;
        To = to ?? string.Empty;
        Token = token ?? string.Empty;

        if (!HasEveryValue())
        {
            ShowInvalidLink();
        }
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!HasEveryValue())
        {
            ShowInvalidLink();
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
            ShowRefusal(commandResult.Error);
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

    /// <summary>
    /// Only a dead link is called dead (#869). A taken previous address is a different problem with a
    /// different answer: nothing was changed, the password still works, and only support can sort it
    /// out. Saying so tells the visitor nothing they could not learn by trying to register that address,
    /// and they already proved they hold a valid revert link for this account. A failed save keeps the
    /// form, because the link is still good and one more click can finish the undo.
    /// </summary>
    private void ShowRefusal(Error error)
    {
        switch (error.Code)
        {
            case "Auth.UserAlreadyExistsByEmail":
                ShowPreviousAddressTaken();
                break;
            case "Auth.EmailChangeFailed":
                ErrorTitle = "Nie udało się cofnąć zmiany";
                ErrorMessage = "Nic nie zmieniliśmy, bo w tej samej chwili na koncie zapisało się coś innego. "
                               + "Kliknij przycisk jeszcze raz. Jeśli błąd się powtórzy, napisz do nas.";
                CanRetry = true;
                break;
            default:
                ShowInvalidLink();
                break;
        }
    }

    private void ShowInvalidLink()
    {
        ErrorTitle = "Link wygasł lub jest nieprawidłowy";
        ErrorMessage = "Linku cofającego zmianę adresu można użyć tylko raz i działa on przez 14 dni "
                       + "od zmiany adresu. Jeśli nadal nie masz dostępu do konta, skontaktuj się z nami.";
    }

    private void ShowPreviousAddressTaken()
    {
        ErrorTitle = "Poprzedni adres należy już do innego konta";
        ErrorMessage = "Nie możemy wrócić na poprzedni adres, bo korzysta z niego inne konto. "
                       + "Nic nie zmieniliśmy — Twoje hasło działa tak jak wcześniej. "
                       + "Napisz do nas, a pomożemy odzyskać konto.";
    }

    /// <summary>
    /// The link's values are printed on this page and then acted on, so they are checked for shape
    /// before either happens. It keeps arbitrary text off a branded page that carries a button which
    /// clears a password, and it keeps a non-GUID id out of Identity's key conversion, which would
    /// throw.
    /// </summary>
    private bool HasEveryValue() =>
        Guid.TryParse(UserId, out _)
        && !string.IsNullOrWhiteSpace(Token)
        && EmailLinkValue.LooksLikeAnAddress(From)
        && EmailLinkValue.LooksLikeAnAddress(To);

    [LoggerMessage(EventId = EventIds.EmailChangeRevertedViaUi, Level = LogLevel.Information, Message = "E-mail change reverted via UI for {MaskedEmail}. Redirecting to the forced password reset.")]
    private static partial void LogEmailChangeRevertedViaUi(ILogger logger, string maskedEmail);
}
