using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Services.RateLimiting;
using LotroKoniecDev.AuthSystem.API.Services.ResponseTiming;
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
    /// A hash computed up front, so every path verifies exactly one hash.
    /// </summary>
    private static readonly string DummyPasswordHash =
        new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), "DummyP@ssw0rd!");

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly AuthDbContext _db;
    private readonly OutboxWriter _outboxWriter;
    private readonly IPasswordResetRequestThrottle _throttle;
    private readonly IResponseTimeFloor _responseTimeFloor;
    private readonly ILogger<ForgotPasswordModel> _logger;

    public ForgotPasswordModel(
        UserManager<ApplicationUser> userManager,
        AuthDbContext db,
        OutboxWriter outboxWriter,
        IPasswordResetRequestThrottle throttle,
        IResponseTimeFloor responseTimeFloor,
        ILogger<ForgotPasswordModel> logger)
    {
        _userManager = userManager;
        _db = db;
        _outboxWriter = outboxWriter;
        _throttle = throttle;
        _responseTimeFloor = responseTimeFloor;
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

        // Only a real account with a send permit writes an outbox row, so every answer waits for the floor
        // (ADR-0059).
        await _responseTimeFloor.HoldAsync(ResponseTimeFloors.AccountLookup, RequestResetAsync);

        // Always show success, so nobody can find out which e-mails are registered.
        IsSubmitted = true;
        return Page();
    }

    private async Task RequestResetAsync()
    {
        ApplicationUser? user = await _userManager.FindByEmailAsync(Email);

        // Every path pays the same PBKDF2 cost. Running the dummy hash only when the user is not
        // found would make real accounts answer measurably faster, because their path is only a cheap
        // outbox insert, and the response time would then tell an attacker which accounts exist
        // (ADR-0038 decision 5). Under the floor this is the second layer (ADR-0059 §5).
        _ = _userManager.PasswordHasher.VerifyHashedPassword(
            new ApplicationUser(), DummyPasswordHash, "DummyP@ssw0rd!");

        // The send budget is taken only where a mail would really go out, so it counts sends and an
        // address nobody registered never spends anything. A refused request answers with the same panel
        // as every other branch here, so it looks exactly like the address being unknown.
        if (user is not null && _throttle.TryAcquire(user))
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
    }

    [LoggerMessage(EventId = EventIds.PasswordResetRequestQueued, Level = LogLevel.Information, Message = "Password reset request queued for user {UserId}")]
    private static partial void LogPasswordResetRequestQueued(ILogger logger, Guid userId);

    [LoggerMessage(EventId = EventIds.PasswordResetRequestThrottled, Level = LogLevel.Warning, Message = "Password reset request throttled for {Email}: the send budget is spent")]
    private static partial void LogPasswordResetThrottled(ILogger logger, string email);
}
