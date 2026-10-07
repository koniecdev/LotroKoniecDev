using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Infrastructure.PwnedPasswords;

namespace LotroKoniecDev.AuthSystem.API.Services.Accounts;

/// <summary>
/// Refuses a password that is in a known data breach (#694, ADR-0065). Identity runs every password
/// validator on each path that sets a password: registration, password change, password reset and the
/// admin seed. Login sets no password, so a password that leaks later never locks anyone out.
/// </summary>
internal sealed class BreachedPasswordValidator : IPasswordValidator<ApplicationUser>
{
    internal const string ErrorCode = "PasswordFoundInBreaches";

    private static readonly PasswordValidator<ApplicationUser> PolicyRules = new();

    private readonly IPwnedPasswordChecker _pwnedPasswordChecker;

    public BreachedPasswordValidator(IPwnedPasswordChecker pwnedPasswordChecker)
    {
        _pwnedPasswordChecker = pwnedPasswordChecker;
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

        PwnedPasswordVerdict verdict = await _pwnedPasswordChecker.CheckAsync(password, CancellationToken.None);

        // Fail-open, on purpose: when the service is down or slow, the password goes through unchecked and
        // the checker logs a warning. A registration form that refuses everyone while someone else's
        // service is down is an outage we caused, and the rest of the password policy still applies.
        return verdict is PwnedPasswordVerdict.Breached
            ? IdentityResult.Failed(new IdentityError
            {
                Code = ErrorCode,
                Description = "This password appears in known data breaches. Choose a different one."
            })
            : IdentityResult.Success;
    }
}
