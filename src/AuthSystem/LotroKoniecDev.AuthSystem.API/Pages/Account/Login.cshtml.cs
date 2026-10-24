using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Services.Gdpr;
using LotroKoniecDev.AuthSystem.API.Services.RateLimiting;
using LotroKoniecDev.AuthSystem.API.Services.ResponseTiming;
using LotroKoniecDev.AuthSystem.API.Settings;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

namespace LotroKoniecDev.AuthSystem.API.Pages.Account;

internal sealed partial class LoginModel : PageModel
{
    /// <summary>
    /// One message for every failure a caller can reach without the password (no such user, locked out,
    /// wrong password, and a spent deletion login budget, #881), so none reveals whether the address is
    /// registered. Cases reached only after the password was verified name their reason (ADR-0046).
    /// </summary>
    private const string GenericCredentialErrorMessage = "Nieprawidłowy e-mail lub hasło.";

    /// <summary>
    /// Shown only after <c>CheckPasswordAsync</c> succeeded, so it reveals nothing to a caller without the
    /// password (ADR-0046). A vague message would send the user to a password reset, which cannot help.
    /// </summary>
    private const string EmailNotConfirmedErrorMessage =
        "To konto nie zostało jeszcze aktywowane. Sprawdź skrzynkę — wysłaliśmy na Twój adres link aktywacyjny.";

    /// <summary>
    /// Shown once the deletion date has passed and the account waits for the daily run (ADR-0031). The
    /// cancel and undo windows have ended, so it names no date or link. The owner chose this text (#916).
    /// </summary>
    private const string DeletionUnderwayMessage = "Usuwanie Twojego konta jest w toku i nie można go już anulować.";

    /// <summary>
    /// The frontend's login route, which turns our auth cookie into an application session through a
    /// silent OIDC challenge. Keep it in step with the Frontend's
    /// <c>AuthenticationDependencyInjectionExtensions.LoginPath</c>.
    /// </summary>
    private const string FrontendLoginPath = "/auth/login";

    /// <summary>
    /// For the failure paths that would otherwise skip hashing, so they cost as much as a wrong password.
    /// The second layer under the time floor (ADR-0059 §5).
    /// </summary>
    private static readonly string DummyPasswordHash =
        new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), "DummyP@ssw0rd!");

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IOptions<OpenIddictSettings> _openIddictSettings;
    private readonly IAccountDeletionSchedule _deletionSchedule;
    private readonly IDeletionScheduledLoginThrottle _deletionScheduledLoginThrottle;
    private readonly IResponseTimeFloor _responseTimeFloor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LoginModel> _logger;

    public LoginModel(
        UserManager<ApplicationUser> userManager,
        IOptions<OpenIddictSettings> openIddictSettings,
        IAccountDeletionSchedule deletionSchedule,
        IDeletionScheduledLoginThrottle deletionScheduledLoginThrottle,
        IResponseTimeFloor responseTimeFloor,
        TimeProvider timeProvider,
        ILogger<LoginModel> logger)
    {
        _userManager = userManager;
        _openIddictSettings = openIddictSettings;
        _deletionSchedule = deletionSchedule;
        _deletionScheduledLoginThrottle = deletionScheduledLoginThrottle;
        _responseTimeFloor = responseTimeFloor;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    [BindProperty]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    public bool RememberMe { get; set; }

    /// <summary>
    /// Usually the <c>/connect/authorize</c> URL the user came from. Printed or used as a redirect only
    /// after <see cref="LocalReturnUrl.Sanitize"/> accepts it.
    /// </summary>
    public string? ReturnUrl { get; set; }

    /// <summary>
    /// Used when a sign-in has nowhere else to go: this host's root answers with discovery JSON, a dead end
    /// for a browser. Null when the web client is not configured, and the sign-in falls back to the root.
    /// </summary>
    public string? FrontendLoginUrl =>
        FrontendUrl.For(_openIddictSettings.Value.WebClient, FrontendLoginPath);

    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Set only for an unconfirmed address after the password was verified (ADR-0046), and taken from the
    /// database, not the posted form, so no arbitrary input reaches a URL.
    /// </summary>
    public string? ResendConfirmationEmail { get; set; }

    public void OnGet(string? returnUrl = null)
    {
        ReturnUrl = LocalReturnUrl.Sanitize(returnUrl);
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        ReturnUrl = LocalReturnUrl.Sanitize(returnUrl);

        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Adres e-mail i hasło są wymagane.";
            return Page();
        }

        // Every answer with the general message waits for the floor (ADR-0059). Only a verified password
        // skips the wait: the answers it can reach need the password anyway.
        ApplicationUser? user = await _responseTimeFloor.HoldAsync(
            ResponseTimeFloors.AccountLookup,
            FindUserWithVerifiedPasswordAsync,
            skipWaitFor: verifiedUser => verifiedUser is not null);

        if (user is null)
        {
            ErrorMessage = GenericCredentialErrorMessage;
            return Page();
        }

        // The exact message is safe: the password was verified.
        if (user.DeletionScheduledAt is not null)
        {
            LogDeletionScheduled(_logger, user.Id, HttpContext.Connection.RemoteIpAddress);
            DateTimeOffset deletionTime =
                _deletionSchedule.FinalizesAt(user.DeletionScheduledAt.Value, user.EmailChangeRevertArmedAt);
            ErrorMessage = _deletionSchedule.IsDueAt(
                    user.DeletionScheduledAt.Value, user.EmailChangeRevertArmedAt, _timeProvider.GetUtcNow())
                ? DeletionUnderwayMessage
                : $"Twoje konto jest zaplanowane do usunięcia dnia {deletionTime.ToPolandDateTimeText()}. " +
                  "Jeśli chcesz je zachować, kliknij w link anulujący usunięcie, który wysłaliśmy na Twój adres e-mail.";
            return Page();
        }

        if (!await _userManager.IsEmailConfirmedAsync(user))
        {
            LogEmailNotConfirmed(_logger, user.Id, HttpContext.Connection.RemoteIpAddress);
            // CheckPasswordAsync skips RequireConfirmedEmail, so it is checked here. Only after the
            // password check: before it, this message would tell anyone which accounts exist (ADR-0046).
            ErrorMessage = EmailNotConfirmedErrorMessage;
            ResendConfirmationEmail = user.Email;
            return Page();
        }

        await _userManager.ResetAccessFailedCountAsync(user);

        // Lets SecurityStampCookieValidator drop this session when the stamp changes; otherwise a valid
        // cookie could keep issuing tokens after a password reset (SEC-03).
        string securityStamp = await _userManager.GetSecurityStampAsync(user);

        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.UserName!),
            new(ClaimTypes.Email, user.Email ?? string.Empty),
            new(_userManager.Options.ClaimsIdentity.SecurityStampClaimType, securityStamp)
        ];

        IList<string> roles = await _userManager.GetRolesAsync(user);
        foreach (string role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        ClaimsIdentity identity = new(claims, IdentityConstants.ApplicationScheme);
        ClaimsPrincipal principal = new(identity);

        DateTimeOffset now = _timeProvider.GetUtcNow();
        await HttpContext.SignInAsync(
            IdentityConstants.ApplicationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = RememberMe,
                IssuedUtc = now,
                ExpiresUtc = RememberMe ? now.AddDays(30) : null
            });

        LogUserLoggedIn(_logger, user.Id);

        if (ReturnUrl is not null)
        {
            return LocalRedirect(ReturnUrl);
        }

        return FrontendLoginUrl is { } frontendLoginUrl
            ? Redirect(frontendLoginUrl)
            : LocalRedirect("/");
    }

    /// <summary>
    /// Null for every failure a caller can reach without the password. Each verifies exactly one hash and
    /// sets no message, so the caller shows the same one for all.
    /// </summary>
    private async Task<ApplicationUser?> FindUserWithVerifiedPasswordAsync()
    {
        // A trailing space from a paste or autofill must not turn a valid login into "no such user".
        ApplicationUser? user = await _userManager.FindByEmailAsync(Email.Trim());

        if (user is null)
        {
            VerifyDummyPassword();
            LogUserNotFound(_logger, Email.MaskEmail(), HttpContext.Connection.RemoteIpAddress);
            return null;
        }

        // Before the lockout check, because a scheduled deletion also locks the account. A wrong password
        // here does not count: Identity would replace the grace-period lock with its own short one (#861).
        // A per-account budget slows guessing instead (#881).
        if (user.DeletionScheduledAt is not null)
        {
            // Before the password check, so a refusal says nothing about the password.
            if (!_deletionScheduledLoginThrottle.TryAcquire(user.Id))
            {
                VerifyDummyPassword();
                LogDeletionScheduledThrottled(_logger, user.Id, HttpContext.Connection.RemoteIpAddress);
                return null;
            }

            if (!await _userManager.HasPasswordAsync(user))
            {
                VerifyDummyPassword();
            }

            if (!await _userManager.CheckPasswordAsync(user, Password))
            {
                LogWrongPassword(_logger, user.Id, HttpContext.Connection.RemoteIpAddress);
                return null;
            }

            return user;
        }

        if (await _userManager.IsLockedOutAsync(user))
        {
            VerifyDummyPassword();
            LogAccountLockedOut(_logger, user.Id, HttpContext.Connection.RemoteIpAddress);
            return null;
        }

        // CheckPasswordAsync returns at once for an account with no password, such as the seeded admin
        // before its first reset (ADR-0056).
        if (!await _userManager.HasPasswordAsync(user))
        {
            VerifyDummyPassword();
        }

        if (!await _userManager.CheckPasswordAsync(user, Password))
        {
            await _userManager.AccessFailedAsync(user);
            LogWrongPassword(_logger, user.Id, HttpContext.Connection.RemoteIpAddress);
            return null;
        }

        return user;
    }

    private void VerifyDummyPassword()
    {
        _ = _userManager.PasswordHasher.VerifyHashedPassword(new ApplicationUser(), DummyPasswordHash, Password);
    }

    [LoggerMessage(EventId = EventIds.LoginUserNotFound, Level = LogLevel.Warning, Message = "Failed login: user not found. Email: {Email}, IP: {IP}")]
    private static partial void LogUserNotFound(ILogger logger, string email, System.Net.IPAddress? ip);

    [LoggerMessage(EventId = EventIds.LoginAccountLockedOut, Level = LogLevel.Warning, Message = "Account locked out. UserId: {UserId}, IP: {IP}")]
    private static partial void LogAccountLockedOut(ILogger logger, Guid userId, System.Net.IPAddress? ip);

    [LoggerMessage(EventId = EventIds.LoginWrongPassword, Level = LogLevel.Warning, Message = "Failed login: wrong password. UserId: {UserId}, IP: {IP}")]
    private static partial void LogWrongPassword(ILogger logger, Guid userId, System.Net.IPAddress? ip);

    [LoggerMessage(EventId = EventIds.LoginEmailNotConfirmed, Level = LogLevel.Warning, Message = "Failed login: email not confirmed. UserId: {UserId}, IP: {IP}")]
    private static partial void LogEmailNotConfirmed(ILogger logger, Guid userId, System.Net.IPAddress? ip);

    [LoggerMessage(EventId = EventIds.LoginSuccessful, Level = LogLevel.Information, Message = "User {UserId} logged in successfully")]
    private static partial void LogUserLoggedIn(ILogger logger, Guid userId);

    [LoggerMessage(EventId = EventIds.LoginDeletionScheduled, Level = LogLevel.Information, Message = "Login blocked: account deletion is scheduled. UserId: {UserId}, IP: {IP}")]
    private static partial void LogDeletionScheduled(ILogger logger, Guid userId, System.Net.IPAddress? ip);

    [LoggerMessage(EventId = EventIds.LoginDeletionScheduledThrottled, Level = LogLevel.Warning, Message = "Failed login: the login budget of an account with a scheduled deletion is spent. UserId: {UserId}, IP: {IP}")]
    private static partial void LogDeletionScheduledThrottled(ILogger logger, Guid userId, System.Net.IPAddress? ip);
}
