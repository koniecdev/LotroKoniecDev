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
    /// The one message for every login failure a caller can reach without proving they know the
    /// password: no such user, locked out, and wrong password. All three say the same thing, so none of
    /// them reveals whether the address is registered.
    /// The cases that only happen after the password was verified, a scheduled deletion or an
    /// unconfirmed address, name their reason instead. ADR-0046 explains why that is safe.
    /// </summary>
    private const string GenericCredentialErrorMessage = "Nieprawidłowy e-mail lub hasło.";

    /// <summary>
    /// Only shown after <c>CheckPasswordAsync</c> succeeded, so it tells a caller who does not know the
    /// password nothing about the address (ADR-0046). Being vague here did real harm: it sent a user
    /// whose only problem was an unopened activation e-mail to the password reset, which cannot fix
    /// their account.
    /// </summary>
    private const string EmailNotConfirmedErrorMessage =
        "To konto nie zostało jeszcze aktywowane. Sprawdź skrzynkę — wysłaliśmy na Twój adres link aktywacyjny.";

    /// <summary>
    /// A hash computed up front for the failure paths that would otherwise skip password hashing, no
    /// such user and locked out. They then take as long as the wrong-password path, so the response
    /// time tells the caller nothing.
    /// </summary>
    private static readonly string DummyPasswordHash =
        new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), "DummyP@ssw0rd!");

    /// <summary>
    /// The frontend's own login route. It turns the auth cookie we just issued into an application
    /// session through a silent OIDC challenge. It copies the Frontend's
    /// <c>AuthenticationDependencyInjectionExtensions.LoginPath</c>. The two contexts share no code, so
    /// renaming it there has to be repeated here, like the register page's <c>/regulamin</c> link.
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
    /// Where to continue after login, taken from the OIDC flow. It is usually the
    /// <c>/connect/authorize</c> URL the user was sent away from, and the form carries it so a
    /// successful login goes back there. It is only printed into the page, or used as a redirect
    /// target, after <see cref="LocalReturnUrl.Sanitize"/> accepts it, which blocks open redirects.
    /// </summary>
    public string? ReturnUrl { get; set; }

    /// <summary>
    /// The absolute URL of the frontend's login route. It is only used when a sign-in has nowhere else
    /// to continue: this host's own root answers with the API discovery JSON, which is a dead end for a
    /// browser coming from the reset-password or confirm-email pages.
    /// The target comes from configuration we trust and never from the request. It is null when the web
    /// client is not configured, for example on a bare test host, and the sign-in then falls back to
    /// the local root.
    /// </summary>
    public string? FrontendLoginUrl =>
        FrontendUrl.For(_openIddictSettings.Value.WebClient, FrontendLoginPath);

    public string? ErrorMessage { get; set; }

    /// <summary>
    /// The address the "resend the activation link" button uses. It is set only when the address is
    /// unconfirmed, so the link never appears unless the password was verified (ADR-0046). It comes
    /// from the database and not from the posted form: the page has no reason to put arbitrary input
    /// into a URL.
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

        // Trim, like the register page's Email.Trim(). A trailing space from a paste or from autofill
        // must not turn a valid login into "no such user".
        ApplicationUser? user = await _userManager.FindByEmailAsync(Email.Trim());

        if (user is null)
        {
            // Hash a dummy password anyway, so the response time does not reveal whether the user
            // exists.
            _ = _userManager.PasswordHasher.VerifyHashedPassword(
                new ApplicationUser(), DummyPasswordHash, Password);
            LogUserNotFound(_logger, Email.MaskEmail(), HttpContext.Connection.RemoteIpAddress);
            ErrorMessage = GenericCredentialErrorMessage;
            return Page();
        }

        // An account with a scheduled deletion is also locked out, so this case has to come first. The
        // exact message only appears after the password was verified: with a wrong password the caller
        // gets the same general error as everywhere else.
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
            // Hash a dummy password so this path takes as long as the not-found one. Without it the
            // early return skips the hashing and the response time reveals a locked-out account.
            _ = _userManager.PasswordHasher.VerifyHashedPassword(
                new ApplicationUser(), DummyPasswordHash, Password);
            LogAccountLockedOut(_logger, user.Id, HttpContext.Connection.RemoteIpAddress);
            // The same general message everywhere, so nobody can find out which accounts exist.
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
            // Identity is set up with RequireConfirmedEmail, but this login path uses
            // CheckPasswordAsync, which does not run PreSignInCheck. So we check confirmation here.
            // Where this check sits matters. Saying the address is unconfirmed is only safe because the
            // password was verified above (ADR-0046). Move it before the password check and this
            // message, together with the resend link, tells anyone which accounts exist.
            ErrorMessage = EmailNotConfirmedErrorMessage;
            ResendConfirmationEmail = user.Email;
            return Page();
        }

        await _userManager.ResetAccessFailedCountAsync(user);

        // Carry the Identity security stamp, so the auth cookie's OnValidatePrincipal
        // (SecurityStampCookieValidator) can drop this session as soon as the stamp changes on a
        // password reset, change or delete. Without it a cookie that is still valid could keep issuing
        // fresh tokens (SEC-03).
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
