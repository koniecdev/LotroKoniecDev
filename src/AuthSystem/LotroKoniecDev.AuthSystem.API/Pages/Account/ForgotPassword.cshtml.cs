using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Services.RateLimiting;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;

namespace LotroKoniecDev.AuthSystem.API.Pages.Account;

/// <summary>
/// The page sends mail to whatever address the caller types, so it keeps the strict budget the API twin
/// has instead of the laxer default the Razor group gives every other page.
/// </summary>
[EnableRateLimiting("forgot-password-limit")]
internal sealed partial class ForgotPasswordModel : PageModel
{
    /// <summary>
    /// A hash computed up front, so the not-found path takes as long as the normal one.
    /// </summary>
    private static readonly string DummyPasswordHash =
        new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), "DummyP@ssw0rd!");

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly AuthDbContext _db;
    private readonly OutboxWriter _outboxWriter;
    private readonly IPasswordResetRequestThrottle _throttle;
    private readonly ILogger<ForgotPasswordModel> _logger;

    public ForgotPasswordModel(
        UserManager<ApplicationUser> userManager,
        AuthDbContext db,
        OutboxWriter outboxWriter,
        IPasswordResetRequestThrottle throttle,
        ILogger<ForgotPasswordModel> logger)
    {
        _userManager = userManager;
        _db = db;
        _outboxWriter = outboxWriter;
        _throttle = throttle;
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

        // Every path pays the same PBKDF2 cost. Running the dummy hash only when the user is not
        // found would make real accounts answer measurably faster, because their path is only a cheap
        // outbox insert, and the response time would then tell an attacker which accounts exist
        // (ADR-0038 decision 5).
        _ = _userManager.PasswordHasher.VerifyHashedPassword(
            new ApplicationUser(), DummyPasswordHash, "DummyP@ssw0rd!");

        // The per-account budget is taken only where a mail would really go out, so it counts sends and
        // an address nobody registered never spends anything. A refused request answers with the same
        // panel as every other branch here, so it looks exactly like the address being unknown.
        if (user is not null && _throttle.TryAcquire(user.Id))
        {
            // No token is created here and the deletion window is not checked here. The payload holds
            // only the id, and the dispatch processor creates the token and does that check when it
            // sends (ADR-0038 decision 2).
            _outboxWriter.Enqueue(new PasswordResetRequested(user.Id));
            await _db.SaveChangesAsync(HttpContext.RequestAborted);

            _outboxWriter.NotifyEnqueuedCommitted();

            LogPasswordResetRequestQueued(_logger, user.Id);
        }
        else if (user is not null)
        {
            LogPasswordResetThrottled(_logger, Email.MaskEmail());
        }

        // Always show success, so nobody can find out which e-mails are registered.
        IsSubmitted = true;
        return Page();
    }

    [LoggerMessage(EventId = EventIds.PasswordResetRequestQueued, Level = LogLevel.Information, Message = "Password reset request queued for user {UserId}")]
    private static partial void LogPasswordResetRequestQueued(ILogger logger, Guid userId);

    [LoggerMessage(EventId = EventIds.PasswordResetRequestThrottled, Level = LogLevel.Warning, Message = "Password reset request throttled for {Email}: the per-account send budget is spent")]
    private static partial void LogPasswordResetThrottled(ILogger logger, string email);
}
