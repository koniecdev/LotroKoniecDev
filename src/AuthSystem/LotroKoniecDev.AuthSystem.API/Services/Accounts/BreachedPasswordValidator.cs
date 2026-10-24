using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Infrastructure.PwnedPasswords;

namespace LotroKoniecDev.AuthSystem.API.Services.Accounts;

/// <summary>
/// Identity runs it on every path that sets a password (ADR-0066). Login sets none, so a password that
/// leaks later never locks anyone out.
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

        // A password that breaks another rule is refused by that rule, whose message names it. Nearly every
        // weak password is in a breach too, so asking would only add a call and a vaguer message
        // (ADR-0066, #1110).
        if (!await PassesEveryOtherRuleAsync(manager, user, password))
        {
            return IdentityResult.Success;
        }

        // Identity hands a validator no token, so the call stops with the request; the admin seed has no
        // request and waits.
        CancellationToken requestAborted = _httpContextAccessor.HttpContext?.RequestAborted ?? CancellationToken.None;

        PwnedPasswordVerdict verdict = await _pwnedPasswordChecker.CheckAsync(password, requestAborted);

        // Fail-open: when the service is down or slow, the password goes through unchecked and the checker
        // logs a warning. Refusing everyone while someone else's service is down would be our outage.
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
            // By type, not instance: any copy of this validator would call back here without end.
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
