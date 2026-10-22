using System.Collections.Concurrent;
using LotroKoniecDev.AuthSystem.API.Services.Emails;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;

/// <summary>
/// Keeps every password-reset mail together with the inbox it went to, and a test reads only the inbox
/// of its own account. A reset mail from an earlier test can still arrive after <see cref="Reset"/>, and
/// a spy that kept only the last token handed it to the next test as its own (#950).
/// </summary>
#pragma warning disable CA1515
public sealed class SpyPasswordResetEmailSender : IPasswordResetEmailSender
#pragma warning restore CA1515
{
    private readonly ConcurrentQueue<ResetEmail> _resetEmails = new();

    /// <summary>
    /// Counts the mails to every inbox, a late one from an earlier test included. Use it only when the
    /// test has no account whose inbox it could read, such as a payload that names no account, and only
    /// on a host where every test that shares this spy waits for each reset mail it causes.
    /// </summary>
    public int ResetEmailCountToAnyInbox => _resetEmails.Count;

    public Task<Result> SendPasswordResetEmailAsync(Guid userId, string email, string resetToken, CancellationToken cancellationToken)
    {
        _resetEmails.Enqueue(new ResetEmail(userId, email, resetToken));
        return Task.FromResult(Result.Success());
    }

    public IReadOnlyList<ResetEmail> ResetEmailsTo(string recipient) =>
        _resetEmails.Where(mail => mail.Recipient == recipient).ToList();

    /// <summary>
    /// The token from the newest reset mail to this inbox, or null when none came.
    /// </summary>
    public string? LastResetTokenSentTo(string recipient) =>
        ResetEmailsTo(recipient).LastOrDefault()?.ResetToken;

    /// <summary>
    /// Waits until a reset mail reaches this inbox, and throws when the time runs out. Any mail since the
    /// last <see cref="Reset"/> counts, so a test that expects a second mail to one inbox resets in
    /// between.
    /// </summary>
    public Task WaitForCaptureAsync(string recipient, TimeSpan? timeout = null) =>
        MailWait.UntilAsync(
            () => _resetEmails.Any(mail => mail.Recipient == recipient),
            $"A password-reset mail to {recipient}",
            timeout);

    public void Reset() => _resetEmails.Clear();

    public sealed record ResetEmail(Guid UserId, string Recipient, string ResetToken);
}
