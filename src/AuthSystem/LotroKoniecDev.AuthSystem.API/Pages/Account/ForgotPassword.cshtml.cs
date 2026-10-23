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
/// The page mails whatever address the caller types, so it shares the API twin's strict budget instead
/// of the default page limit.
/// </summary>
[EnableRateLimiting("forgot-password-limit")]
internal sealed partial class ForgotPasswordModel : PageModel
{
    /// <summary>
    /// So every path verifies exactly one hash.
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

        // Only a real account with a permit writes an outbox row (ADR-0059).
        await _responseTimeFloor.HoldAsync(ResponseTimeFloors.AccountLookup, RequestResetAsync);

        // Success on every branch, so nobody can learn which e-mails are registered.
        IsSubmitted = true;
        return Page();
    }

    private async Task RequestResetAsync()
    {
        ApplicationUser? user = await _userManager.FindByEmailAsync(Email);

        // On every path: a real account's path is only a cheap outbox insert. The second layer under the
        // floor (ADR-0059 §5).
        _ = _userManager.PasswordHasher.VerifyHashedPassword(
            new ApplicationUser(), DummyPasswordHash, "DummyP@ssw0rd!");

        // Only where a mail would really go out, so an unknown address spends nothing. A refusal shows the
        // same panel as every other branch.
        if (user is not null && _throttle.TryAcquire(user))
        {
            // The dispatch processor creates the token and checks the deletion window when it sends
            // (ADR-0038 decision 2).
            _outboxWriter.Enqueue(new PasswordResetRequested(user.Id));
            await _db.SaveChangesAsync(HttpContext.RequestAborted);

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
