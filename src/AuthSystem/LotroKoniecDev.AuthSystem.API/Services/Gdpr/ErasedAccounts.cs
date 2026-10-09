using System.Linq.Expressions;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.Constants;

namespace LotroKoniecDev.AuthSystem.API.Services.Gdpr;

/// <summary>
/// The one rule for "this account has been erased", for code that acts on erased accounts after the
/// fact (ADR-0065). An account counts only with what every erasure has written: the <c>anon-</c>
/// address on the anonymization domain, and no password. The address alone is not proof, because the
/// registration form accepts any address, and a registered account always has a password. The deletion
/// date is not part of it: the immediate deletion before two-phase deletion (#460) never wrote one.
/// </summary>
internal static class ErasedAccounts
{
    public static readonly Expression<Func<ApplicationUser, bool>> Rule = account =>
        account.Email != null
        && account.Email.StartsWith(AnonymizationConstants.EmailPrefix)
        && account.Email.EndsWith(AnonymizationConstants.EmailDomain)
        && account.PasswordHash == null;

    private static readonly Func<ApplicationUser, bool> CompiledRule = Rule.Compile();

    public static bool Includes(ApplicationUser account) => CompiledRule(account);
}
