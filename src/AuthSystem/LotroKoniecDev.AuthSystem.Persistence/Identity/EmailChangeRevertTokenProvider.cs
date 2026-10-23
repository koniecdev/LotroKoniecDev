using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

namespace LotroKoniecDev.AuthSystem.Persistence.Identity;

/// <summary>
/// Fourteen days, because the person who needs this link has just been locked out and may not read that
/// mailbox daily. A scheduled deletion waits for this undo, and 14 days is about the most that keeps the
/// erasure inside one month, so do not make it longer (ADR-0031, #946 amendment).
/// </summary>
public sealed class EmailChangeRevertTokenProviderOptions
{
    public TimeSpan TokenLifespan { get; set; } = TimeSpan.FromDays(14);
}

/// <summary>
/// Creates the token behind the "to nie ja" link sent to the old address after an e-mail change
/// (ADR-0048). Following it restores the previous address and clears the password.
/// </summary>
/// <remarks>
/// <para>
/// It does not extend <see cref="DataProtectorTokenProvider{TUser}"/>, because that provider binds the
/// token to the security stamp. An attacker who took over the address changes the password, which
/// rotates the stamp, so a stamp-bound revert link would be dead before the owner read the mail.
/// </para>
/// <para>
/// It binds to <see cref="ApplicationUser.EmailChangeRevertStamp"/> instead, which rotates only on a
/// successful revert. That makes the links single-use: after a chain of changes an attacker holds a
/// revert token too, and without this stamp they could undo the owner's recovery.
/// </para>
/// </remarks>
public sealed class EmailChangeRevertTokenProvider : IUserTwoFactorTokenProvider<ApplicationUser>
{
    public const string ProviderName = "EmailChangeRevert";

    private const string ProtectorPurpose = "LotroKoniecDev.AuthSystem.EmailChangeRevert.v1";

    private readonly IDataProtector _protector;
    private readonly TimeProvider _timeProvider;
    private readonly EmailChangeRevertTokenProviderOptions _options;

    public EmailChangeRevertTokenProvider(
        IDataProtectionProvider dataProtectionProvider,
        TimeProvider timeProvider,
        IOptions<EmailChangeRevertTokenProviderOptions> options)
    {
        _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
        _timeProvider = timeProvider;
        _options = options.Value;
    }

    public static string PurposeFor(string previousEmail, string newEmail) =>
        $"RevertEmailChange:{previousEmail}->{newEmail}";

    public Task<string> GenerateAsync(string purpose, UserManager<ApplicationUser> manager, ApplicationUser user)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(user);

        string payload = string.Join(
            '|',
            _timeProvider.GetUtcNow().UtcTicks.ToString(CultureInfo.InvariantCulture),
            user.Id.ToString(),
            StampOf(user),
            purpose ?? string.Empty);

        // Base64, like every Identity-issued token here. The link factory runs it through
        // LinkGenerator, which escapes it for the query string.
        return Task.FromResult(Convert.ToBase64String(_protector.Protect(Encoding.UTF8.GetBytes(payload))));
    }

    public Task<bool> ValidateAsync(
        string purpose,
        string token,
        UserManager<ApplicationUser> manager,
        ApplicationUser user)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(user);

        if (string.IsNullOrWhiteSpace(token))
        {
            return Task.FromResult(false);
        }

        byte[] payload;
        try
        {
            payload = _protector.Unprotect(Convert.FromBase64String(token));
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            // Tampered, or signed by a key ring we no longer hold: both mean "refuse".
            return Task.FromResult(false);
        }

        string[] parts = Encoding.UTF8.GetString(payload).Split('|', 4);
        if (parts.Length != 4)
        {
            return Task.FromResult(false);
        }

        if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long createdAtTicks))
        {
            return Task.FromResult(false);
        }

        DateTimeOffset createdAt = new(createdAtTicks, TimeSpan.Zero);
        if (createdAt + _options.TokenLifespan < _timeProvider.GetUtcNow())
        {
            return Task.FromResult(false);
        }

        bool matches = string.Equals(parts[1], user.Id.ToString(), StringComparison.Ordinal)
                       && string.Equals(parts[2], StampOf(user), StringComparison.Ordinal)
                       && string.Equals(parts[3], purpose ?? string.Empty, StringComparison.Ordinal);

        return Task.FromResult(matches);
    }

    /// <summary>
    /// An account never reverted has no stamp, so all its tokens share the empty value. The first
    /// successful revert sets one and retires them all.
    /// </summary>
    private static string StampOf(ApplicationUser user) =>
        user.EmailChangeRevertStamp?.ToString() ?? string.Empty;

    /// <summary>
    /// This provider only backs an e-mailed link, so nothing may offer it as a login step.
    /// </summary>
    public Task<bool> CanGenerateTwoFactorTokenAsync(UserManager<ApplicationUser> manager, ApplicationUser user) =>
        Task.FromResult(false);
}
