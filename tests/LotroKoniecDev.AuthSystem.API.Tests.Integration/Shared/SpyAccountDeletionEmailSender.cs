using System.Collections.Concurrent;
using LotroKoniecDev.AuthSystem.API.Services.Emails;
using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;

/// <summary>
/// Keeps every deletion e-mail together with the inbox it went to, and a test reads only the inbox of
/// its own account. The e-mails travel through the outbox after the request has ended, so a mail from an
/// earlier test can still arrive after <see cref="Reset"/>. When the spy kept only the last mail and one
/// count for all inboxes, such a late mail could pass for the test's own (#911).
/// </summary>
#pragma warning disable CA1515
public sealed class SpyAccountDeletionEmailSender : IAccountDeletionEmailSender
#pragma warning restore CA1515
{
    private readonly ConcurrentQueue<ScheduledEmail> _scheduledEmails = new();
    private readonly ConcurrentQueue<PreviousAddressNotice> _previousAddressNotices = new();
    private readonly ConcurrentQueue<string> _cancelledEmailRecipients = new();

    public bool ShouldFailScheduledEmail { get; set; }

    /// <summary>
    /// Counts the mails to every inbox, a late one from an earlier test included. Use it only when the
    /// test has no account whose inbox it could read, such as a payload that names no account, and only
    /// on a host where every test that shares this spy waits for each deletion mail it causes.
    /// </summary>
    public int ScheduledEmailCountToAnyInbox => _scheduledEmails.Count;

    /// <inheritdoc cref="ScheduledEmailCountToAnyInbox"/>
    public int CancelledEmailCountToAnyInbox => _cancelledEmailRecipients.Count;

    public Task<Result> SendDeletionScheduledEmailAsync(
        Guid userId,
        string email,
        string cancelToken,
        DateTimeOffset finalizesAt,
        CancellationToken cancellationToken)
    {
        _scheduledEmails.Enqueue(new ScheduledEmail(email, cancelToken, finalizesAt));

        return ShouldFailScheduledEmail
            ? Task.FromResult(Result.Failure(new Error("Test.EmailFailed", "Simulated email failure")))
            : Task.FromResult(Result.Success());
    }

    public Task<Result> SendDeletionScheduledNoticeToPreviousAddressAsync(
        Guid userId,
        string previousEmail,
        string currentEmail,
        string cancelToken,
        DateTimeOffset finalizesAt,
        CancellationToken cancellationToken)
    {
        _previousAddressNotices.Enqueue(new PreviousAddressNotice(previousEmail, currentEmail, cancelToken));

        return Task.FromResult(Result.Success());
    }

    public Task<Result> SendDeletionCancelledEmailAsync(Guid userId, string email, CancellationToken cancellationToken)
    {
        _cancelledEmailRecipients.Enqueue(email);
        return Task.FromResult(Result.Success());
    }

    public IReadOnlyList<ScheduledEmail> ScheduledEmailsTo(string recipient) =>
        _scheduledEmails.Where(mail => mail.Recipient == recipient).ToList();

    public IReadOnlyList<PreviousAddressNotice> PreviousAddressNoticesTo(string recipient) =>
        _previousAddressNotices.Where(notice => notice.Recipient == recipient).ToList();

    public int CancelledEmailCountTo(string recipient) =>
        _cancelledEmailRecipients.Count(address => address == recipient);

    /// <summary>
    /// The cancel token from the newest deletion-scheduled mail to this inbox, or null when none came.
    /// </summary>
    public string? LastCancelTokenSentTo(string recipient) =>
        ScheduledEmailsTo(recipient).LastOrDefault()?.CancelToken;

    /// <summary>
    /// Waits until a deletion-scheduled mail reaches this inbox or the time runs out. The request only
    /// commits an outbox row (commit -> relay -> delivery -> this spy), so the mail has to be waited for.
    /// Any mail since the last <see cref="Reset"/> counts, so a test that schedules twice for one inbox
    /// resets in between. It returns either way, and the assertions stay in the test.
    /// </summary>
    public Task WaitForScheduledCaptureAsync(string recipient, TimeSpan? timeout = null) =>
        WaitForAsync(() => _scheduledEmails.Any(mail => mail.Recipient == recipient), timeout);

    /// <summary>
    /// Waits until a notice reaches the address an armed undo would restore. A test that expects the
    /// notice waits for it by itself, so it does not depend on the order the processor sends in.
    /// A test that expects no notice waits for the delivery's inbox row instead.
    /// </summary>
    public Task WaitForPreviousAddressNoticeAsync(string recipient, TimeSpan? timeout = null) =>
        WaitForAsync(() => _previousAddressNotices.Any(notice => notice.Recipient == recipient), timeout);

    /// <summary>
    /// Waits until a deletion-cancelled mail reaches this inbox or the time runs out. That notice travels
    /// the same pipeline as the scheduled mail.
    /// </summary>
    public Task WaitForCancelledCaptureAsync(string recipient, TimeSpan? timeout = null) =>
        WaitForAsync(() => _cancelledEmailRecipients.Contains(recipient), timeout);

    public void Reset()
    {
        _scheduledEmails.Clear();
        _previousAddressNotices.Clear();
        _cancelledEmailRecipients.Clear();
        ShouldFailScheduledEmail = false;
    }

    private static async Task WaitForAsync(Func<bool> arrived, TimeSpan? timeout)
    {
        using CancellationTokenSource waitWindow = new(timeout ?? TimeSpan.FromSeconds(15));

        while (!arrived() && !waitWindow.IsCancellationRequested)
        {
            await Task.Delay(50);
        }
    }

    public sealed record ScheduledEmail(string Recipient, string CancelToken, DateTimeOffset FinalizesAt);

    public sealed record PreviousAddressNotice(string Recipient, string CurrentEmail, string CancelToken);
}
