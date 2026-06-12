using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

namespace LotroKoniecDev.AuthSystem.API.Pages.Account;

[EnableRateLimiting("auth-endpoint-limit")]
internal sealed partial class ConfirmEmailModel : PageModel
{
    /// <summary>
    /// Pre-computed hash for timing-equalization when user is not found.
    /// </summary>
    private static readonly string DummyPasswordHash =
        new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), "DummyP@ssw0rd!");

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<ConfirmEmailModel> _logger;

    public ConfirmEmailModel(
        UserManager<ApplicationUser> userManager,
        ILogger<ConfirmEmailModel> logger)
    {
        _userManager = userManager;
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

        ApplicationUser? user = await _userManager.FindByEmailAsync(Email);

        if (user is null)
        {
            // Perform dummy work to prevent timing-based user enumeration
            _ = new PasswordHasher<ApplicationUser>()
                .VerifyHashedPassword(new ApplicationUser(), DummyPasswordHash, "DummyP@ssw0rd!");

            ErrorMessage = "Link potwierdzający jest nieprawidłowy lub wygasł.";
            return;
        }

        if (user.EmailConfirmed)
        {
            IsCompleted = true;
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
            ErrorMessage = "Link potwierdzający jest nieprawidłowy lub wygasł.";
            return;
        }

        ErrorMessage = string.Join(" ", result.Errors.Select(e => e.Description));
    }

    [LoggerMessage(EventId = EventIds.EmailConfirmedViaUi, Level = LogLevel.Information, Message = "Email confirmed via UI for user {UserId}.")]
    private static partial void LogEmailConfirmedViaUi(ILogger logger, Guid userId);
}
