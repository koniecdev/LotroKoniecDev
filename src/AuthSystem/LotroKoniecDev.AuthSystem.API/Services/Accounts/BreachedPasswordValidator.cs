using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Infrastructure.PwnedPasswords;

namespace LotroKoniecDev.AuthSystem.API.Services.Accounts;

/// <summary>
/// Refuses a password that is in a known data breach (#694, ADR-0065). Identity runs every password
/// validator on each path that sets a password: registration, password change, password reset and the
/// admin seed. Login sets no password, so a password that leaks later never locks anyone out.
/// </summary>
internal sealed partial class BreachedPasswordValidator : IPasswordValidator<ApplicationUser>
{
    internal const string ErrorCode = "PasswordFoundInBreaches";

    private static readonly PasswordValidator<ApplicationUser> PolicyRules = new();

    private readonly IPwnedPasswordChecker _pwnedPasswordChecker;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<BreachedPasswordValidator> _logger;

    public BreachedPasswordValidator(
        IPwnedPasswordChecker pwnedPasswordChecker,
        IHttpContextAccessor httpContextAccessor,
        ILogger<BreachedPasswordValidator> logger)
    {
        _pwnedPasswordChecker = pwnedPasswordChecker;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public async Task<IdentityResult> ValidateAsync(
        UserManager<ApplicationUser> manager,
        ApplicationUser user,
        string? password)
    {
        // Refusing an empty password is the built-in validator's job.
        if (string.IsNullOrEmpty(password))
        {
            return IdentityResult.Success;
        }

        // A password that breaks the policy is already refused by the built-in validator, whose error
        // names the rule. Nearly every weak password is in a breach too, so asking would only cost a call
        // and hide that rule behind a vaguer message on the reset page.
        IdentityResult policyResult = await PolicyRules.ValidateAsync(manager, user, password);
        if (!policyResult.Succeeded)
        {
            return IdentityResult.Success;
        }

        // Identity hands a validator no token. A visitor who gave up must not keep the registration's
        // transaction open for the whole time limit, so the call stops with the request. The admin seed
        // runs outside a request and waits.
        CancellationToken requestAborted = _httpContextAccessor.HttpContext?.RequestAborted ?? CancellationToken.None;

        PwnedPasswordVerdict verdict = await _pwnedPasswordChecker.CheckAsync(password, requestAborted);

        // Fail-open, on purpose: when the service is down or slow, the password goes through unchecked and
        // the checker logs a warning. A registration form that refuses everyone while someone else's
        // service is down is an outage we caused, and the rest of the password policy still applies.
        if (verdict is not PwnedPasswordVerdict.Breached)
        {
            return IdentityResult.Success;
        }

        LogPasswordRefusedAsBreached(_logger);

        return IdentityResult.Failed(new IdentityError
        {
            Code = ErrorCode,
            Description = "This password appears in known data breaches. Choose a different one."
        });
    }

    [LoggerMessage(EventId = EventIds.PasswordRefusedAsBreached, Level = LogLevel.Information, Message = "A new password was refused because it appears in known data breaches")]
    private static partial void LogPasswordRefusedAsBreached(ILogger logger);
}
