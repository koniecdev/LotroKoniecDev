using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Infrastructure.PwnedPasswords;

namespace LotroKoniecDev.AuthSystem.API.Services.Accounts;

/// <summary>
/// Refuses a password that is in a known data breach (#694, ADR-0066). Identity runs every password
/// validator on each path that sets a password: registration, password change, password reset and the
/// admin seed. Login sets no password, so a password that leaks later never locks anyone out.
/// </summary>
internal sealed partial class BreachedPasswordValidator : IPasswordValidator<ApplicationUser>
{
    internal const string ErrorCode = "PasswordFoundInBreaches";

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

        // A password that breaks another rule is already refused by that rule's validator, whose error
        // names the rule. Nearly every weak password is in a breach too, so asking would only cost a call
        // and add a vaguer message to the rule's own. Every other validator counts, not only the built-in
        // policy (ADR-0066, amended for #1110).
        if (!await PassesEveryOtherRuleAsync(manager, user, password))
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

    private static async Task<bool> PassesEveryOtherRuleAsync(
        UserManager<ApplicationUser> manager,
        ApplicationUser user,
        string password)
    {
        foreach (IPasswordValidator<ApplicationUser> validator in manager.PasswordValidators)
        {
            // Skipped by type, not by instance: any copy of this validator called from here would call
            // this one back, without end.
            if (validator is BreachedPasswordValidator)
            {
                continue;
            }

            IdentityResult result = await validator.ValidateAsync(manager, user, password);
            if (!result.Succeeded)
            {
                return false;
            }
        }

        return true;
    }

    [LoggerMessage(EventId = EventIds.PasswordRefusedAsBreached, Level = LogLevel.Information, Message = "A new password was refused because it appears in known data breaches")]
    private static partial void LogPasswordRefusedAsBreached(ILogger logger);
}
