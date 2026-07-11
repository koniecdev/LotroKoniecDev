using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LotroKoniecDev.AuthSystem.API.Services.Emails;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

namespace LotroKoniecDev.AuthSystem.API.Pages.Account;

internal sealed partial class ForgotPasswordModel : PageModel
{
    /// <summary>
    /// Pre-computed hash for timing-equalization when user is not found.
    /// </summary>
    private static readonly string DummyPasswordHash =
        new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), "DummyP@ssw0rd!");

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IPasswordResetEmailSender _emailSender;
    private readonly ILogger<ForgotPasswordModel> _logger;

    public ForgotPasswordModel(
        UserManager<ApplicationUser> userManager,
        IPasswordResetEmailSender emailSender,
        ILogger<ForgotPasswordModel> logger)
    {
        _userManager = userManager;
        _emailSender = emailSender;
        _logger = logger;
    }

    [BindProperty]
    public string Email { get; set; } = string.Empty;

    public bool IsSubmitted { get; set; }

    public void OnGet()
    {
        IsSubmitted = false;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(Email))
        {
            ModelState.AddModelError(string.Empty, "Adres email jest wymagany.");
            return Page();
        }

        ApplicationUser? user = await _userManager.FindByEmailAsync(Email);

        if (user is null)
        {
            // Perform dummy work to prevent timing-based user enumeration
            _ = _userManager.PasswordHasher.VerifyHashedPassword(
                new ApplicationUser(), DummyPasswordHash, "DummyP@ssw0rd!");
        }
        else
        {
            string token = await _userManager.GeneratePasswordResetTokenAsync(user);
            await _emailSender.SendPasswordResetEmailAsync(user.Id, Email, token, HttpContext.RequestAborted);
            LogPasswordResetTokenGenerated(_logger, user.Id);
        }

        // Always show success to prevent email enumeration
        IsSubmitted = true;
        return Page();
    }

    [LoggerMessage(EventId = EventIds.PasswordResetTokenGenerated, Level = LogLevel.Information, Message = "Password reset token generated for user {UserId}")]
    private static partial void LogPasswordResetTokenGenerated(ILogger logger, Guid userId);
}
