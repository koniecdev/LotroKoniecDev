using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using LotroKoniecDev.AuthSystem.API.Services.ResponseTiming;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

namespace LotroKoniecDev.AuthSystem.API.Pages.Account;

[EnableRateLimiting("auth-endpoint-limit")]
internal sealed partial class ConfirmEmailModel : PageModel
{
    private const string InvalidOrExpiredLinkMessage = "Link potwierdzający jest nieprawidłowy lub wygasł.";

    /// <summary>
    /// A hash computed up front, so every path verifies exactly one hash.
    /// </summary>
    private static readonly string DummyPasswordHash =
        new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), "DummyP@ssw0rd!");

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IResponseTimeFloor _responseTimeFloor;
    private readonly ILogger<ConfirmEmailModel> _logger;

    public ConfirmEmailModel(
        UserManager<ApplicationUser> userManager,
        IResponseTimeFloor responseTimeFloor,
        ILogger<ConfirmEmailModel> logger)
    {
        _userManager = userManager;
        _responseTimeFloor = responseTimeFloor;
        _logger = logger;
    }

    public string Email { get; set; } = string.Empty;

    public string Token { get; set; } = string.Empty;

    public bool IsCompleted { get; set; }

    public string? ErrorMessage { get; set; }

    public async Task OnGet(string? email = null, string? token = null)
    {
        Email = email ?? string.Empty;
        Token = token ?? string.Empty;

        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Token))
        {
            ErrorMessage = "Link potwierdzający jest nieprawidłowy.";
            return;
        }

        // A real account never hashed here and fails at the cheap token check, so every answer waits for
        // the floor (ADR-0059).
        ResponseTimer responseTimer = _responseTimeFloor.Start(ResponseTimeFloors.AccountLookup);
        try
        {
            await ConfirmAsync();
        }
        finally
        {
            await responseTimer.WaitForFloorAsync(HttpContext.RequestAborted);
        }
    }

    private async Task ConfirmAsync()
    {
        ApplicationUser? user = await _userManager.FindByEmailAsync(Email);

        // Every path pays the same PBKDF2 cost, the second layer under the floor (ADR-0059 §5).
        _ = _userManager.PasswordHasher.VerifyHashedPassword(
            new ApplicationUser(), DummyPasswordHash, "DummyP@ssw0rd!");

        if (user is null)
        {
            ErrorMessage = InvalidOrExpiredLinkMessage;
            return;
        }

        if (user.EmailConfirmed)
        {
            // The success page names the account, so it needs the mailed token too: without this check
            // any address with a confirmed account showed "confirmed" (ADR-0059 §7). Confirming does not
            // change the security stamp, so the owner's second click on the link still gets here.
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

        ErrorMessage = string.Join(" ", result.Errors.Select(e => e.Description));
    }

    [LoggerMessage(EventId = EventIds.EmailConfirmedViaUi, Level = LogLevel.Information, Message = "Email confirmed via UI for user {UserId}.")]
    private static partial void LogEmailConfirmedViaUi(ILogger logger, Guid userId);
}
