using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Features.Auth;
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

    public string? ErrorMessage { get; set; }

    public void OnGet(string? userId = null, string? email = null, string? token = null)
    {
        UserId = userId ?? string.Empty;
        Email = email ?? string.Empty;
        Token = token ?? string.Empty;

        if (string.IsNullOrWhiteSpace(UserId) || string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Token))
        {
            ErrorMessage = "Link potwierdzający zmianę adresu jest nieprawidłowy.";
        }
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(UserId) || string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Token))
        {
            ErrorMessage = "Link potwierdzający zmianę adresu jest nieprawidłowy.";
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
            ErrorMessage = "Link potwierdzający zmianę adresu jest nieprawidłowy lub wygasł.";
            return Page();
        }

        LogEmailChangeConfirmedViaUi(_logger, Email.MaskEmail());

        IsCompleted = true;
        return Page();
    }

    [LoggerMessage(EventId = EventIds.EmailChangeConfirmedViaUi, Level = LogLevel.Information, Message = "E-mail change confirmed via UI for {MaskedEmail}.")]
    private static partial void LogEmailChangeConfirmedViaUi(ILogger logger, string maskedEmail);
}
