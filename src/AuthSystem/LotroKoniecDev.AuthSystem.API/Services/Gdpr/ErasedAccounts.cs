using System.Linq.Expressions;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.Constants;

namespace LotroKoniecDev.AuthSystem.API.Services.Gdpr;

/// <summary>
/// The one rule for "this account has been erased" (ADR-0065): the <c>anon-</c> address on the
/// anonymization domain and no password, which every erasure wrote. Not the deletion date: the
/// immediate deletion before #460 never wrote one.
/// </summary>
/// <remarks>
/// Neither half is proof alone. A live account can register with the marker address, but it can never
/// confirm it, so it never reaches the flows that clear a password. The exception is the seeded admin,
/// which has no password (ADR-0056): never give it an address on the marker domain.
/// </remarks>
internal static class ErasedAccounts
{
    public static readonly Expression<Func<ApplicationUser, bool>> Rule = account =>
        account.Email != null
        && account.Email.StartsWith(AnonymizationConstants.EmailPrefix)
        && account.Email.EndsWith(AnonymizationConstants.EmailDomain)
        && account.PasswordHash == null;

    /// <summary>
    /// <see cref="Rule"/> for an account in memory, compared ordinally like the SQL. A compiled
    /// <see cref="Rule"/> would compare by culture, which ignores characters such as a soft hyphen.
    /// </summary>
    public static bool Includes(ApplicationUser account) =>
        account.Email is { } email
        && email.StartsWith(AnonymizationConstants.EmailPrefix, StringComparison.Ordinal)
        && email.EndsWith(AnonymizationConstants.EmailDomain, StringComparison.Ordinal)
        && account.PasswordHash is null;
}
