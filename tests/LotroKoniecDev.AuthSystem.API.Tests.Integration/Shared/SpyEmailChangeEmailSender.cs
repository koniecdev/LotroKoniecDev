using System.Collections.Concurrent;
using LotroKoniecDev.AuthSystem.API.Services.Emails;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;

/// <summary>
/// Keeps every mail of the e-mail change flow together with the inbox it went to, so tests can follow
/// the links a real user would receive. Half of ADR-0048 is about the old mailbox getting told, so a test
/// checks which inbox got which mail, not only the token. A test reads only the inboxes of its own
/// change: the mails travel through the outbox after the request has ended, so a mail from an earlier
/// test can still arrive after <see cref="Reset"/>, and a spy that kept only the last mail of each kind
/// handed it to the next test as its own (#950).
/// </summary>
#pragma warning disable CA1515
public sealed class SpyEmailChangeEmailSender : IEmailChangeEmailSender
#pragma warning restore CA1515
{
    private readonly ConcurrentQueue<Verification> _verifications = new();
    private readonly ConcurrentQueue<Warning> _warnings = new();
    private readonly ConcurrentQueue<ChangedNotice> _changedNotices = new();
    private readonly ConcurrentQueue<RevertOffer> _revertOffers = new();
    private readonly ConcurrentQueue<string> _addressTakenNoticeRecipients = new();

    public Task<Result> SendVerificationAsync(
        Guid userId, string newEmail, string verificationToken, CancellationToken cancellationToken)
    {
        _verifications.Enqueue(new Verification(newEmail, verificationToken));
        return Task.FromResult(Result.Success());
    }

    public Task<Result> SendAddressTakenNoticeAsync(
        Guid ownerUserId, string ownerEmail, CancellationToken cancellationToken)
    {
        _addressTakenNoticeRecipients.Enqueue(ownerEmail);
        return Task.FromResult(Result.Success());
    }

    public Task<Result> SendChangeRequestedWarningAsync(
        Guid userId, string currentEmail, string newEmail, CancellationToken cancellationToken)
    {
        _warnings.Enqueue(new Warning(currentEmail, newEmail));
        return Task.FromResult(Result.Success());
    }

    public Task<Result> SendChangedNoticeAsync(
        Guid userId, string newEmail, string previousEmail, CancellationToken cancellationToken)
    {
        _changedNotices.Enqueue(new ChangedNotice(newEmail, previousEmail));
        return Task.FromResult(Result.Success());
    }

    public Task<Result> SendChangedNoticeWithRevertAsync(
        Guid userId,
        string previousEmail,
        string newEmail,
        string revertToken,
        TimeSpan revertWindow,
        CancellationToken cancellationToken)
    {
        _revertOffers.Enqueue(new RevertOffer(previousEmail, newEmail, revertToken));
        return Task.FromResult(Result.Success());
    }

    public IReadOnlyList<Verification> VerificationsTo(string recipient) =>
        _verifications.Where(mail => mail.Recipient == recipient).ToList();

    /// <summary>
    /// The link token from the newest verification mail to this inbox, or null when none came.
    /// </summary>
    public string? LastVerificationTokenSentTo(string recipient) =>
        VerificationsTo(recipient).LastOrDefault()?.Token;

    public IReadOnlyList<Warning> WarningsTo(string recipient) =>
        _warnings.Where(mail => mail.Recipient == recipient).ToList();

    public IReadOnlyList<ChangedNotice> ChangedNoticesTo(string recipient) =>
        _changedNotices.Where(mail => mail.Recipient == recipient).ToList();

    public IReadOnlyList<RevertOffer> RevertOffersTo(string recipient) =>
        _revertOffers.Where(mail => mail.Recipient == recipient).ToList();

    /// <summary>
    /// The undo token from the newest revert offer to this inbox, or null when none came.
    /// </summary>
    public string? LastRevertTokenSentTo(string recipient) =>
        RevertOffersTo(recipient).LastOrDefault()?.RevertToken;

    public int AddressTakenNoticeCountTo(string recipient) =>
        _addressTakenNoticeRecipients.Count(address => address == recipient);

    /// <summary>
    /// Waits until a verification link reaches the new address, and throws when the time runs out. Any
    /// mail since the last <see cref="Reset"/> counts, so a test that asks twice for one address resets in
    /// between.
    /// </summary>
    public Task WaitForVerificationCaptureAsync(string newEmail, TimeSpan? timeout = null) =>
        MailWait.UntilAsync(
            () => _verifications.Any(mail => mail.Recipient == newEmail),
            $"A verification link to {newEmail}",
            timeout);

    /// <summary>
    /// Waits for the warning to the current address. It goes out first, before the link or the notice
    /// (#860), so a test that has seen the warning may not have seen the mail to the new address yet.
    /// </summary>
    public Task WaitForChangeRequestedWarningCaptureAsync(string currentEmail, TimeSpan? timeout = null) =>
        MailWait.UntilAsync(
            () => _warnings.Any(mail => mail.Recipient == currentEmail),
            $"A change-requested warning to {currentEmail}",
            timeout);

    /// <summary>
    /// Waits for the notice to the owner of a new address that already has an account, sent instead of
    /// the link (#860).
    /// </summary>
    public Task WaitForAddressTakenNoticeCaptureAsync(string ownerEmail, TimeSpan? timeout = null) =>
        MailWait.UntilAsync(
            () => _addressTakenNoticeRecipients.Contains(ownerEmail),
            $"An address-taken notice to {ownerEmail}",
            timeout);

    /// <summary>
    /// Waits for the notice sent to the new address. Every confirmed change sends it, after the undo link
    /// when the change arms one. A helper that completes a change waits for the mails of that change, so
    /// none of them is still on its way when the test goes on.
    /// </summary>
    public Task WaitForChangedNoticeCaptureAsync(string newEmail, TimeSpan? timeout = null) =>
        MailWait.UntilAsync(
            () => _changedNotices.Any(mail => mail.Recipient == newEmail),
            $"A changed notice to {newEmail}",
            timeout);

    /// <summary>
    /// Waits for both mails of a change that arms an undo link: the offer to the previous address and the
    /// notice to the new one. It is one wait and not two in a row, so a refused confirm, which sends
    /// neither mail, runs out once and not twice.
    /// </summary>
    public Task WaitForRevertOfferAndNoticeCaptureAsync(
        string previousEmail, string newEmail, TimeSpan? timeout = null) =>
        MailWait.UntilAsync(
            () => _revertOffers.Any(mail => mail.Recipient == previousEmail && mail.TargetAddress == newEmail)
                  && _changedNotices.Any(mail => mail.Recipient == newEmail),
            $"The revert offer to {previousEmail} and the changed notice to {newEmail}",
            timeout);

    public void Reset()
    {
        _verifications.Clear();
        _warnings.Clear();
        _changedNotices.Clear();
        _revertOffers.Clear();
        _addressTakenNoticeRecipients.Clear();
    }

    public sealed record Verification(string Recipient, string Token);

    public sealed record Warning(string Recipient, string TargetAddress);

    public sealed record ChangedNotice(string Recipient, string PreviousEmail);

    public sealed record RevertOffer(string Recipient, string TargetAddress, string RevertToken);
}
