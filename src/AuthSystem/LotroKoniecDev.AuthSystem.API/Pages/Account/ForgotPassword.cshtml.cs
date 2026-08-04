using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;

namespace LotroKoniecDev.AuthSystem.API.Pages.Account;

internal sealed partial class ForgotPasswordModel : PageModel
{
    /// <summary>
    /// Pre-computed hash for timing-equalization when user is not found.
    /// </summary>
    private static readonly string DummyPasswordHash =
        new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), "DummyP@ssw0rd!");

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly AuthDbContext _db;
    private readonly OutboxWriter _outboxWriter;
    private readonly ILogger<ForgotPasswordModel> _logger;

    public ForgotPasswordModel(
        UserManager<ApplicationUser> userManager,
        AuthDbContext db,
        OutboxWriter outboxWriter,
        ILogger<ForgotPasswordModel> logger)
    {
        _userManager = userManager;
        _db = db;
        _outboxWriter = outboxWriter;
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

        // Every path pays the same PBKDF2 cost. Burning the dummy hash only on the
        // not-found branch would make existing accounts answer measurably FASTER
        // (their path is just a cheap outbox insert), turning response time into an
        // inverted user-enumeration oracle (ADR-0038 decision 5).
        _ = _userManager.PasswordHasher.VerifyHashedPassword(
            new ApplicationUser(), DummyPasswordHash, "DummyP@ssw0rd!");

        if (user is not null)
        {
            // No token minting and no deletion-window check here: the payload carries the id
            // alone, and the dispatch processor mints the token and owns the guard at delivery
            // (ADR-0038 decision 2).
            _outboxWriter.Enqueue(new PasswordResetRequested(user.Id));
            await _db.SaveChangesAsync(HttpContext.RequestAborted);

            _outboxWriter.NotifyEnqueuedCommitted();

            LogPasswordResetRequestQueued(_logger, user.Id);
        }

        // Always show success to prevent email enumeration
        IsSubmitted = true;
        return Page();
    }

    [LoggerMessage(EventId = EventIds.PasswordResetRequestQueued, Level = LogLevel.Information, Message = "Password reset request queued for user {UserId}")]
    private static partial void LogPasswordResetRequestQueued(ILogger logger, Guid userId);
}
