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
/// The page the "confirm your new address" link leads to. A GET only shows a confirmation form, so a
/// mail scanner that opens the link changes no address. The POST applies the change.
/// </summary>
[EnableRateLimiting("auth-endpoint-limit")]
internal sealed partial class ConfirmEmailChangeModel : PageModel
{
    private readonly ICommandHandler<ConfirmEmailChange.Command, Result> _handler;
    private readonly ILogger<ConfirmEmailChangeModel> _logger;

    public ConfirmEmailChangeModel(
        ICommandHandler<ConfirmEmailChange.Command, Result> handler,
        ILogger<ConfirmEmailChangeModel> logger)
    {
        _handler = handler;
        _logger = logger;
    }

    [BindProperty]
    public string UserId { get; set; } = string.Empty;

    [BindProperty]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    public string Token { get; set; } = string.Empty;

    public bool IsCompleted { get; set; }

    public string? ErrorTitle { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>
    /// The link passed its token check and only the save failed, so the page shows the form again and
    /// one more click can finish the change (#869).
    /// </summary>
    public bool CanRetry { get; set; }

    public void OnGet(string? userId = null, string? email = null, string? token = null)
    {
        UserId = userId ?? string.Empty;
        Email = email ?? string.Empty;
        Token = token ?? string.Empty;

        if (!HasUsableLinkValues())
        {
            ShowInvalidLink("Link potwierdzający zmianę adresu jest nieprawidłowy.");
        }
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!HasUsableLinkValues())
        {
            ShowInvalidLink("Link potwierdzający zmianę adresu jest nieprawidłowy.");
            return Page();
        }

        ConfirmEmailChange.Command command = new(
            UserId,
            Email,
            Token,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            HttpContext.Request.Headers.UserAgent.ToString());

        Result commandResult = await _handler.Handle(command, HttpContext.RequestAborted);

        if (commandResult.IsFailure)
        {
            ShowRefusal(commandResult.Error);
            return Page();
        }

        LogEmailChangeConfirmedViaUi(_logger, Email.MaskEmail());

        IsCompleted = true;
        return Page();
    }

    /// <summary>
    /// Only a dead link is called dead, because the visitor acts on what the page says: a good link
    /// called dead sends them off for a new one, when one more click would have worked (#869). The
    /// other answers come only after the token check, and asking for the change already tells the
    /// owner about a taken address or a scheduled deletion, so naming them here reveals nothing new.
    /// </summary>
    private void ShowRefusal(Error error)
    {
        switch (error.Code)
        {
            case "Auth.UserAlreadyExistsByEmail":
                ErrorTitle = "Ten adres należy już do innego konta";
                ErrorMessage = "Nie możemy przenieść konta na ten adres, bo korzysta z niego inne konto. "
                               + "Nic nie zmieniliśmy — nadal logujesz się dotychczasowym adresem.";
                break;
            case "Auth.DeletionAlreadyScheduled":
                ErrorTitle = "Usunięcie konta jest już zaplanowane";
                ErrorMessage = "Nie możemy zmienić adresu, bo to konto czeka na usunięcie. Nic nie zmieniliśmy. "
                               + "Usunięcie możesz anulować wyłącznie przez link wysłany na dotychczasowy adres konta. "
                               + "Potem poproś o zmianę adresu jeszcze raz.";
                break;
            case "Auth.EmailChangeFailed":
                ErrorTitle = "Nie udało się zapisać zmiany";
                ErrorMessage = "Nie zmieniliśmy adresu, bo w tej samej chwili na koncie zapisało się coś innego. "
                               + "Kliknij przycisk jeszcze raz. Jeśli błąd się powtórzy, napisz do nas.";
                CanRetry = true;
                break;
            default:
                ShowInvalidLink("Link potwierdzający zmianę adresu jest nieprawidłowy lub wygasł.");
                break;
        }
    }

    private void ShowInvalidLink(string reason)
    {
        ErrorTitle = "Link wygasł lub jest nieprawidłowy";
        ErrorMessage = reason + " Link jest ważny 24 godziny i można go użyć tylko raz. "
                              + "Zaloguj się i poproś o zmianę adresu jeszcze raz.";
    }

    /// <summary>
    /// The link's values are printed on this page and then acted on, so they are checked for shape
    /// before either happens. It keeps arbitrary text off a branded page that carries a real button,
    /// and it keeps a non-GUID id out of Identity's key conversion, which would throw.
    /// </summary>
    private bool HasUsableLinkValues() =>
        Guid.TryParse(UserId, out _)
        && !string.IsNullOrWhiteSpace(Token)
        && EmailLinkValue.LooksLikeAnAddress(Email);

    [LoggerMessage(EventId = EventIds.EmailChangeConfirmedViaUi, Level = LogLevel.Information, Message = "E-mail change confirmed via UI for {MaskedEmail}.")]
    private static partial void LogEmailChangeConfirmedViaUi(ILogger logger, string maskedEmail);
}
