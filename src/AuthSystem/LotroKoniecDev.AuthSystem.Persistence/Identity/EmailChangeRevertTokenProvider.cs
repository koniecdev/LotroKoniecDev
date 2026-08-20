using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

namespace LotroKoniecDev.AuthSystem.Persistence.Identity;

/// <summary>
/// Options for the revert token. Fourteen days is long on purpose: the person who needs this link is
/// someone who has just been locked out of their own account and may not read that mailbox daily.
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
/// It does <b>not</b> extend <see cref="DataProtectorTokenProvider{TUser}"/>, and that is the whole
/// point. That provider writes the user's security stamp into the token and refuses the token once
/// the stamp moves. Changing the password rotates the stamp, and changing the password is exactly
/// what an attacker does after taking over the address — so a stamp-bound revert link would already
/// be dead by the time the real owner read their mail. This one protects the creation time, the user
/// id and the purpose, and nothing else.
/// </para>
/// <para>
/// What it does carry instead is
/// <see cref="ApplicationUser.EmailChangeRevertStamp"/>, which rotates on a successful revert and on
/// nothing else. That is what makes these links single-use, and it is not optional: every revert
/// token is a bearer credential to move the account to its previous address, and after a chain of
/// changes an attacker holds one too. Without this field their token stays live and they simply undo
/// the owner's recovery. Rotating on revert kills every token in the chain at once.
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
            // A token that was tampered with, or was signed by a key ring we no longer hold, is simply
            // not a token. Both look the same from here and both mean "refuse".
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
    /// Never a two-factor option. This provider only backs a link we e-mail, so nothing may offer it
    /// as a login step.
    /// </summary>
    /// <summary>
    /// An account that has never been reverted has no stamp yet, and every token it issues shares that
    /// same empty value. The first successful revert sets one, which is what retires them all.
    /// </summary>
    private static string StampOf(ApplicationUser user) =>
        user.EmailChangeRevertStamp?.ToString() ?? string.Empty;

    public Task<bool> CanGenerateTwoFactorTokenAsync(UserManager<ApplicationUser> manager, ApplicationUser user) =>
        Task.FromResult(false);
}
