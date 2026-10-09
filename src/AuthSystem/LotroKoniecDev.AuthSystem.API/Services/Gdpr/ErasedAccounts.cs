using System.Linq.Expressions;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.Constants;

namespace LotroKoniecDev.AuthSystem.API.Services.Gdpr;

/// <summary>
/// The one rule for "this account has been erased", for code that acts on erased accounts after the
/// fact (ADR-0065). An account counts only with what every erasure has written: the <c>anon-</c>
/// address on the anonymization domain, and no password. The deletion date is not part of it: the
/// immediate deletion before two-phase deletion (#460) never wrote one.
/// </summary>
/// <remarks>
/// Neither half is proof alone. The registration form accepts any address, so a live account can carry
/// the marker address, but it keeps its password: no mail reaches that domain, so the account can never
/// confirm it, and only a confirmed account reaches the two flows that clear a live password (a
/// cancelled deletion and an undone e-mail change). The one exception is the seeded admin, confirmed
/// without a mail and seeded without a password (ADR-0056): never give it an address on the marker
/// domain.
/// </remarks>
internal static class ErasedAccounts
{
    public static readonly Expression<Func<ApplicationUser, bool>> Rule = account =>
        account.Email != null
        && account.Email.StartsWith(AnonymizationConstants.EmailPrefix)
        && account.Email.EndsWith(AnonymizationConstants.EmailDomain)
        && account.PasswordHash == null;

    /// <summary>
    /// <see cref="Rule"/> for an account already in memory. It compares ordinally, as the SQL that
    /// <see cref="Rule"/> becomes compares byte for byte. A compiled <see cref="Rule"/> would compare by
    /// culture, which ignores characters such as a soft hyphen, so the two could disagree.
    /// </summary>
    public static bool Includes(ApplicationUser account) =>
        account.Email is { } email
        && email.StartsWith(AnonymizationConstants.EmailPrefix, StringComparison.Ordinal)
        && email.EndsWith(AnonymizationConstants.EmailDomain, StringComparison.Ordinal)
        && account.PasswordHash is null;
}
