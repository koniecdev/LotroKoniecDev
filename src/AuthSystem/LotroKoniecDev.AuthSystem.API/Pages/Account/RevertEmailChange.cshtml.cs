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
/// Where the "to nie ja" link leads (ADR-0048). A GET only shows a confirmation form: mail security
/// scanners open every URL they see, and a GET that reverted would undo every real e-mail change and
/// clear the password seconds after the notice arrived.
/// </summary>
[EnableRateLimiting("auth-endpoint-limit")]
internal sealed partial class RevertEmailChangeModel : PageModel
{
    private readonly ICommandHandler<RevertEmailChange.Command, Result<RevertEmailChange.RevertedEmailChange>> _handler;
    private readonly UsedLinkNextStepCookie _nextStepCookie;
    private readonly ILogger<RevertEmailChangeModel> _logger;

    public RevertEmailChangeModel(
        ICommandHandler<RevertEmailChange.Command, Result<RevertEmailChange.RevertedEmailChange>> handler,
        UsedLinkNextStepCookie nextStepCookie,
        ILogger<RevertEmailChangeModel> logger)
    {
        _handler = handler;
        _nextStepCookie = nextStepCookie;
        _logger = logger;
    }

    [BindProperty]
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// The previous address the link names. The account goes back to its armed address, which is normally
    /// this one; the handler never trusts the link for it (ADR-0048).
    /// </summary>
    [BindProperty]
    public string From { get; set; } = string.Empty;

    /// <summary>
    /// The address the link was issued for. It is part of the token's purpose, but the account need not
    /// still be on it: a revert after a further change still works.
    /// </summary>
    [BindProperty]
    public string To { get; set; } = string.Empty;

    [BindProperty]
    public string Token { get; set; } = string.Empty;

    public string? ErrorTitle { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>
    /// The token passed and only the save failed, so one more click can finish the undo (#869).
    /// </summary>
    public bool CanRetry { get; set; }

    public IActionResult OnGet(string? userId = null, string? from = null, string? to = null, string? token = null)
    {
        UserId = userId ?? string.Empty;
        From = from ?? string.Empty;
        To = to ?? string.Empty;
        Token = token ?? string.Empty;

        if (!HasEveryValue())
        {
            ShowInvalidLink();
            return Page();
        }

        // Back from the password form loads this used link again, and its button would call it dead (#941).
        return RedirectToNextStepIfUsedHere() ?? Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!HasEveryValue())
        {
            ShowInvalidLink();
            return Page();
        }

        // A form restored from the browser cache can resend a used link. It asks for no input, so the
        // answer is the same as the first one (#941).
        if (RedirectToNextStepIfUsedHere() is { } redirect)
        {
            return redirect;
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

        // The password is gone, so the reset flow is the only way back in, as after a cancelled deletion.
        PasswordResetStep nextStep = new(commandResult.Value.RestoredEmail, commandResult.Value.PasswordResetToken);
        _nextStepCookie.Remember(HttpContext, UsedLinkFlow.EmailChangeRevert, Token, nextStep);
        return RedirectToPasswordReset(nextStep);
    }

    private IActionResult? RedirectToNextStepIfUsedHere() =>
        _nextStepCookie.NextStepFor(Request, UsedLinkFlow.EmailChangeRevert, Token) is { } nextStep
            ? RedirectToPasswordReset(nextStep)
            : null;

    private RedirectToPageResult RedirectToPasswordReset(PasswordResetStep nextStep) =>
        RedirectToPage("/Account/ResetPassword", new { email = nextStep.Email, token = nextStep.Token });

    /// <summary>
    /// Only a dead link is called dead (#869). A taken previous address gets its own answer: nothing
    /// changed and the password still works. Only a holder of a valid revert link gets this far, so this
    /// tells a stranger nothing (#860). A failed save keeps the form, because the link is still good.
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
    /// The link's values are printed and then acted on, so their shape is checked first: no arbitrary text
    /// on a branded page with a button that clears a password, and no non-GUID id for Identity to throw on.
    /// </summary>
    private bool HasEveryValue() =>
        Guid.TryParse(UserId, out _)
        && !string.IsNullOrWhiteSpace(Token)
        && EmailLinkValue.LooksLikeAnAddress(From)
        && EmailLinkValue.LooksLikeAnAddress(To);

    [LoggerMessage(EventId = EventIds.EmailChangeRevertedViaUi, Level = LogLevel.Information, Message = "E-mail change reverted via UI for {MaskedEmail}. Redirecting to the forced password reset.")]
    private static partial void LogEmailChangeRevertedViaUi(ILogger logger, string maskedEmail);
}
