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
    /// Single source of truth for the credential-failure branches a caller can reach without proving
    /// they know the password — user-not-found, lockout and wrong-password. Kept identical across the
    /// three so none of them reveals whether the address is registered (anti-enumeration). The
    /// branches that run behind a verified password (deletion-scheduled, unconfirmed e-mail) name
    /// their reason instead; ADR-0046 has the exposure argument.
    /// </summary>
    private const string GenericCredentialErrorMessage = "Nieprawidłowy e-mail lub hasło.";

    /// <summary>
    /// Reached only after <c>CheckPasswordAsync</c> succeeded, so it cannot tell an unauthenticated
    /// caller anything about the address (ADR-0046). Being vague here was actively harmful: it
    /// pointed a user whose only problem is an unopened activation e-mail at the password reset,
    /// which cannot fix their account.
    /// </summary>
    private const string EmailNotConfirmedErrorMessage =
        "To konto nie zostało jeszcze aktywowane. Sprawdź skrzynkę — wysłaliśmy na Twój adres link aktywacyjny.";

    /// <summary>
    /// Pre-computed hash for timing-equalization on the credential-failure branches that would
    /// otherwise skip password hashing — user-not-found and lockout — so their latency matches the
    /// wrong-password branch and no branch is distinguishable by response time.
    /// </summary>
    private static readonly string DummyPasswordHash =
        new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), "DummyP@ssw0rd!");

    /// <summary>
    /// The frontend's own login route, which turns the freshly issued auth cookie into an application
    /// session through a silent OIDC challenge. Mirrors the Frontend's
    /// <c>AuthenticationDependencyInjectionExtensions.LoginPath</c>; the two contexts share no code, so
    /// a rename there has to be repeated here (same arrangement as the register page's
    /// <c>/regulamin</c> link).
    /// </summary>
    private const string FrontendLoginPath = "/auth/login";

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IOptions<OpenIddictSettings> _openIddictSettings;
    private readonly GdprSettings _gdprSettings;
    private readonly ILogger<LoginModel> _logger;

    public LoginModel(
        UserManager<ApplicationUser> userManager,
        IOptions<OpenIddictSettings> openIddictSettings,
        IOptions<GdprSettings> gdprSettings,
        ILogger<LoginModel> logger)
    {
        _userManager = userManager;
        _openIddictSettings = openIddictSettings;
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

    /// <summary>
    /// Absolute URL of the frontend's login route. Only ever the fallback for a sign-in that carries no
    /// local continuation: this host's own root answers with the API discovery JSON, which dead-ends a
    /// browser arriving from the reset-password or confirm-email pages. The target comes from trusted
    /// configuration, never from the request. Null when the web client is not configured (e.g. a bare
    /// test host) — the sign-in then falls back to the local root.
    /// </summary>
    public string? FrontendLoginUrl =>
        FrontendUrl.For(_openIddictSettings.Value.WebClient, FrontendLoginPath);

    public string? ErrorMessage { get; set; }

    /// <summary>
    /// The address the "resend the activation link" affordance carries, set only on the
    /// unconfirmed-e-mail branch so the link never appears without a verified password (ADR-0046).
    /// Taken from the store rather than from the posted form — the page has no reason to reflect an
    /// arbitrary input into a URL.
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
            // The position of this check is load-bearing, not incidental: naming the unconfirmed state
            // is only safe because the password was verified above (ADR-0046). Moving it in front of
            // the password check turns this message — and the resend link — into an account-enumeration
            // oracle for an unauthenticated caller.
            ErrorMessage = EmailNotConfirmedErrorMessage;
            ResendConfirmationEmail = user.Email;
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

        if (ReturnUrl is not null)
        {
            return LocalRedirect(ReturnUrl);
        }

        return FrontendLoginUrl is { } frontendLoginUrl
            ? Redirect(frontendLoginUrl)
            : LocalRedirect("/");
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
