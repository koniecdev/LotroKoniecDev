using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Settings;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

namespace LotroKoniecDev.AuthSystem.API.Pages.Account;

internal sealed partial class LoginModel : PageModel
{
    /// <summary>
    /// Single source of truth for every credential-failure branch. Kept identical across
    /// user-not-found, lockout, wrong-password and unconfirmed-email so no branch ever reveals
    /// which check failed (anti-enumeration). The activation hint is generic — it neither confirms
    /// the account exists nor that it is unconfirmed — yet still nudges a brand-new user to finish
    /// registration.
    /// </summary>
    private const string GenericCredentialErrorMessage =
        "Nieprawidłowy e-mail lub hasło. Jeśli konto zostało dopiero co utworzone, " +
        "dokończ rejestrację, potwierdzając swój adres e-mail — kliknij link aktywacyjny, który do Ciebie wysłaliśmy.";

    /// <summary>
    /// Pre-computed hash for timing-equalization on the credential-failure branches that would
    /// otherwise skip password hashing — user-not-found and lockout — so their latency matches the
    /// wrong-password branch and no branch is distinguishable by response time.
    /// </summary>
    private static readonly string DummyPasswordHash =
        new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), "DummyP@ssw0rd!");

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly GdprSettings _gdprSettings;
    private readonly ILogger<LoginModel> _logger;

    public LoginModel(
        UserManager<ApplicationUser> userManager,
        IOptions<GdprSettings> gdprSettings,
        ILogger<LoginModel> logger)
    {
        _userManager = userManager;
        _gdprSettings = gdprSettings.Value;
        _logger = logger;
    }

    [BindProperty]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    public bool RememberMe { get; set; }

    /// <summary>
    /// The local continuation captured from the OIDC flow (e.g. the <c>/connect/authorize</c> URL the
    /// user was bounced from), carried through the form so a successful login resumes it. Reflected
    /// into the page and used as the redirect target only after passing
    /// <see cref="LocalReturnUrl.Sanitize"/>, which blocks open redirects.
    /// </summary>
    public string? ReturnUrl { get; set; }

    public string? ErrorMessage { get; set; }

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

        // Trim mirrors the register page's Email.Trim() — a pasted/autofilled trailing space must
        // not turn a valid login into a not-found.
        ApplicationUser? user = await _userManager.FindByEmailAsync(Email.Trim());

        if (user is null)
        {
            // Perform a dummy password hash to prevent timing-based user enumeration
            _ = _userManager.PasswordHasher.VerifyHashedPassword(
                new ApplicationUser(), DummyPasswordHash, Password);
            LogUserNotFound(_logger, Email.MaskEmail(), HttpContext.Connection.RemoteIpAddress);
            ErrorMessage = GenericCredentialErrorMessage;
            return Page();
        }

        // A deletion-scheduled account is also locked out, so this branch must run first.
        // The specific message is revealed only after the password is verified — with a
        // wrong password the caller gets the same generic error as everywhere else.
        if (user.DeletionScheduledAt is not null)
        {
            bool deletionScheduledPasswordValid = await _userManager.CheckPasswordAsync(user, Password);
            if (!deletionScheduledPasswordValid)
            {
                await _userManager.AccessFailedAsync(user);
                LogWrongPassword(_logger, user.Id, HttpContext.Connection.RemoteIpAddress);
                ErrorMessage = GenericCredentialErrorMessage;
                return Page();
            }

            LogDeletionScheduled(_logger, user.Id, HttpContext.Connection.RemoteIpAddress);
            DateTimeOffset deletionDate = user.DeletionScheduledAt.Value + _gdprSettings.DeletionGracePeriod;
            ErrorMessage =
                $"Twoje konto jest zaplanowane do usunięcia dnia {deletionDate.ToPolandTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}. " +
                "Jeśli chcesz je zachować, kliknij w link anulujący usunięcie, który wysłaliśmy na Twój adres e-mail.";
            return Page();
        }

        if (await _userManager.IsLockedOutAsync(user))
        {
            // Perform a dummy password hash so the lockout branch's latency matches the not-found
            // branch — otherwise this early return skips hashing and leaks the locked-out state via timing.
            _ = _userManager.PasswordHasher.VerifyHashedPassword(
                new ApplicationUser(), DummyPasswordHash, Password);
            LogAccountLockedOut(_logger, user.Id, HttpContext.Connection.RemoteIpAddress);
            // Use the same generic message to prevent account enumeration
            ErrorMessage = GenericCredentialErrorMessage;
            return Page();
        }

        bool passwordValid = await _userManager.CheckPasswordAsync(user, Password);

        if (!passwordValid)
        {
            await _userManager.AccessFailedAsync(user);
            LogWrongPassword(_logger, user.Id, HttpContext.Connection.RemoteIpAddress);
            ErrorMessage = GenericCredentialErrorMessage;
            return Page();
        }

        if (!await _userManager.IsEmailConfirmedAsync(user))
        {
            LogEmailNotConfirmed(_logger, user.Id, HttpContext.Connection.RemoteIpAddress);
            // Identity is configured with RequireConfirmedEmail, but the interactive login path uses
            // CheckPasswordAsync, which does not run PreSignInCheck. Enforce confirmation explicitly.
            // Same generic message as the other branches — never reveal that the account exists but is
            // unconfirmed — and placed after the password hash above so there is no timing oracle.
            ErrorMessage = GenericCredentialErrorMessage;
            return Page();
        }

        await _userManager.ResetAccessFailedCountAsync(user);

        // Carry the Identity security stamp so the auth cookie's OnValidatePrincipal
        // (SecurityStampCookieValidator) can evict this session the moment the stamp rotates on a
        // password reset/change/delete — otherwise a still-live cookie could mint fresh tokens (SEC-03).
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

        DateTimeOffset now = DateTimeOffset.UtcNow;
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

        return LocalRedirect(ReturnUrl ?? "/");
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
}
