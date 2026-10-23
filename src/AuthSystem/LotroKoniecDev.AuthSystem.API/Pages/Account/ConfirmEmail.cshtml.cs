using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using LotroKoniecDev.AuthSystem.API.Services.ResponseTiming;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

namespace LotroKoniecDev.AuthSystem.API.Pages.Account;

[EnableRateLimiting("auth-endpoint-limit")]
internal sealed partial class ConfirmEmailModel : PageModel
{
    /// <summary>
    /// The same words for an unknown address and any link that does not verify. The last sentence is for
    /// the owner of an active account who opens an old link (ADR-0059 §7).
    /// </summary>
    private const string InvalidOrExpiredLinkMessage =
        "Link potwierdzający jest nieprawidłowy lub wygasł. Jeśli Twoje konto jest już aktywne, po prostu się zaloguj.";

    private const string InvalidLinkTitle = "Link wygasł lub jest nieprawidłowy";

    private const string ConfirmationFailedTitle = "Nie udało się potwierdzić adresu";

    private const string ConfirmationFailedMessage =
        "Otwórz link z wiadomości jeszcze raz. Jeśli błąd się powtórzy, napisz do nas.";

    /// <summary>
    /// Lets every path verify exactly one hash.
    /// </summary>
    private static readonly string DummyPasswordHash =
        new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), "DummyP@ssw0rd!");

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IResponseTimeFloor _responseTimeFloor;
    private readonly ILogger<ConfirmEmailModel> _logger;

    public string Email { get; set; } = string.Empty;

    public string Token { get; set; } = string.Empty;

    public bool IsCompleted { get; set; }

    /// <summary>
    /// The link verified and Identity refused for another reason, so the page must not call it dead (#869).
    /// </summary>
    public bool LinkWasGood { get; set; }

    public string ErrorTitle => LinkWasGood ? ConfirmationFailedTitle : InvalidLinkTitle;

    public string? ErrorMessage { get; set; }

    public ConfirmEmailModel(
        UserManager<ApplicationUser> userManager,
        IResponseTimeFloor responseTimeFloor,
        ILogger<ConfirmEmailModel> logger)
    {
        _userManager = userManager;
        _responseTimeFloor = responseTimeFloor;
        _logger = logger;
    }

    public async Task OnGet(string? email = null, string? token = null)
    {
        Email = email ?? string.Empty;
        Token = token ?? string.Empty;

        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Token))
        {
            ErrorMessage = "Link potwierdzający jest nieprawidłowy.";
            return;
        }

        // The branches differ in cost: a wrong token fails at a cheap check, and a confirmed address
        // checks only the token (ADR-0059).
        await _responseTimeFloor.HoldAsync(ResponseTimeFloors.AccountLookup, ConfirmAsync);
    }

    private async Task ConfirmAsync()
    {
        ApplicationUser? user = await _userManager.FindByEmailAsync(Email);

        // The dummy hash runs on every path: the second layer under the floor (ADR-0059 §5).
        _ = _userManager.PasswordHasher.VerifyHashedPassword(
            new ApplicationUser(), DummyPasswordHash, "DummyP@ssw0rd!");

        if (user is null)
        {
            ErrorMessage = InvalidOrExpiredLinkMessage;
            return;
        }

        if (user.EmailConfirmed)
        {
            // The success page names the account, so it needs the mailed token too (ADR-0059 §7).
            // Confirming does not change the stamp, so the owner's second click still gets here.
            bool tokenValid = await _userManager.VerifyUserTokenAsync(
                user,
                _userManager.Options.Tokens.EmailConfirmationTokenProvider,
                UserManager<ApplicationUser>.ConfirmEmailTokenPurpose,
                Token);

            if (tokenValid)
            {
                IsCompleted = true;
            }
            else
            {
                ErrorMessage = InvalidOrExpiredLinkMessage;
            }

            return;
        }

        IdentityResult result = await _userManager.ConfirmEmailAsync(user, Token);

        if (result.Succeeded)
        {
            LogEmailConfirmedViaUi(_logger, user.Id);

            IsCompleted = true;
            return;
        }

        if (result.Errors.Any(e => e.Code is "InvalidToken"))
        {
            ErrorMessage = InvalidOrExpiredLinkMessage;
            return;
        }

        // Identity's text is English, so the page never prints it; the log keeps the codes (#1076).
        LogEmailConfirmationFailedViaUi(_logger, user.Id, string.Join(", ", result.Errors.Select(e => e.Code)));
        LinkWasGood = true;
        ErrorMessage = ConfirmationFailedMessage;
    }

    [LoggerMessage(EventId = EventIds.EmailConfirmationFailed, Level = LogLevel.Warning, Message = "Email confirmation failed via UI for user {UserId}. Error codes: {ErrorCodes}")]
    private static partial void LogEmailConfirmationFailedViaUi(ILogger logger, Guid userId, string errorCodes);

    [LoggerMessage(EventId = EventIds.EmailConfirmedViaUi, Level = LogLevel.Information, Message = "Email confirmed via UI for user {UserId}.")]
    private static partial void LogEmailConfirmedViaUi(ILogger logger, Guid userId);
}
