using LotroKoniecDev.AuthSystem.API.Services.Emails;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;

/// <summary>
/// Captures the two tokens of the e-mail change flow, so tests can follow the links a real user would
/// receive. It also records which address each message went to: half of ADR-0048 is about the old
/// mailbox getting told, and a test that only checked the token would not notice if it stopped.
/// </summary>
#pragma warning disable CA1515
public sealed class SpyEmailChangeEmailSender : IEmailChangeEmailSender
#pragma warning restore CA1515
{
    private readonly Lock _lock = new();
    private int _verificationCallCount;
    private int _warningCallCount;
    private int _noticeCallCount;
    private int _revertOfferCallCount;

    public string? LastVerificationRecipient { get; private set; }
    public string? LastVerificationToken { get; private set; }
    public string? LastWarningRecipient { get; private set; }
    public string? LastWarningTargetAddress { get; private set; }
    public string? LastNoticeRecipient { get; private set; }
    public string? LastRevertOfferRecipient { get; private set; }
    public string? LastRevertOfferTargetAddress { get; private set; }
    public string? LastRevertToken { get; private set; }

    public int VerificationCallCount => Volatile.Read(ref _verificationCallCount);
    public int WarningCallCount => Volatile.Read(ref _warningCallCount);
    public int NoticeCallCount => Volatile.Read(ref _noticeCallCount);
    public int RevertOfferCallCount => Volatile.Read(ref _revertOfferCallCount);

    public Task<Result> SendVerificationAsync(
        Guid userId, string newEmail, string verificationToken, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            LastVerificationRecipient = newEmail;
            LastVerificationToken = verificationToken;
            Interlocked.Increment(ref _verificationCallCount);
        }

        return Task.FromResult(Result.Success());
    }

    public Task<Result> SendChangeRequestedWarningAsync(
        Guid userId, string currentEmail, string newEmail, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            LastWarningRecipient = currentEmail;
            LastWarningTargetAddress = newEmail;
            Interlocked.Increment(ref _warningCallCount);
        }

        return Task.FromResult(Result.Success());
    }

    public Task<Result> SendChangedNoticeAsync(
        Guid userId, string newEmail, string previousEmail, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            LastNoticeRecipient = newEmail;
            Interlocked.Increment(ref _noticeCallCount);
        }

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
        lock (_lock)
        {
            LastRevertOfferRecipient = previousEmail;
            LastRevertOfferTargetAddress = newEmail;
            LastRevertToken = revertToken;
            Interlocked.Increment(ref _revertOfferCallCount);
        }

        return Task.FromResult(Result.Success());
    }

    /// <summary>
    /// Waits for the verification link to arrive. The request only commits an outbox row (ADR-0038),
    /// so everything after it — relay, delivery, this spy — has to be waited for, never assumed.
    /// </summary>
    public Task WaitForVerificationCaptureAsync(TimeSpan? timeout = null) =>
        WaitForAsync(() => VerificationCallCount > 0, timeout);

    /// <summary>
    /// Waits for the warning to the current address. It is sent after the verification link, so a
    /// test that has seen the link may not have seen the warning yet (#772).
    /// </summary>
    public Task WaitForChangeRequestedWarningCaptureAsync(TimeSpan? timeout = null) =>
        WaitForAsync(() => WarningCallCount > 0, timeout);

    /// <summary>
    /// Waits for the notice sent to the new address. Every confirmed change sends it, after the undo link
    /// when the change arms one. A helper that completes a change must wait for the mails of that change.
    /// If it does not, a mail can land after the next <see cref="Reset"/>, and the next test takes it for
    /// its own (#950).
    /// </summary>
    public Task WaitForChangedNoticeCaptureAsync(TimeSpan? timeout = null) =>
        WaitForAsync(() => NoticeCallCount > 0, timeout);

    /// <summary>
    /// Waits for both mails of a change that arms an undo link. It is one wait and not two in a row, so a
    /// refused confirm, which sends neither mail, runs out once and not twice.
    /// </summary>
    public Task WaitForRevertOfferAndNoticeCaptureAsync(TimeSpan? timeout = null) =>
        WaitForAsync(() => RevertOfferCallCount > 0 && NoticeCallCount > 0, timeout);

    /// <summary>
    /// Each wait checks the counter of its mail, not a field. A send bumps its counter last, with
    /// <see cref="Interlocked.Increment(ref int)"/>, so a wait that sees the counter also sees the fields.
    /// Sends and <see cref="Reset"/> share one lock, so a send never lands halfway through a reset.
    /// A wait that runs out returns quietly, so a test that then checks for a missing mail must first
    /// check that the mail it waited for is there. It does not throw yet, because a late mail from the
    /// previous test still makes some waits run out (#950).
    /// </summary>
    private static async Task WaitForAsync(Func<bool> arrived, TimeSpan? timeout)
    {
        using CancellationTokenSource waitWindow = new(timeout ?? TimeSpan.FromSeconds(15));

        while (!arrived() && !waitWindow.IsCancellationRequested)
        {
            await Task.Delay(50);
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            LastVerificationRecipient = null;
            LastVerificationToken = null;
            LastWarningRecipient = null;
            LastWarningTargetAddress = null;
            LastNoticeRecipient = null;
            LastRevertOfferRecipient = null;
            LastRevertOfferTargetAddress = null;
            LastRevertToken = null;
            Interlocked.Exchange(ref _verificationCallCount, 0);
            Interlocked.Exchange(ref _warningCallCount, 0);
            Interlocked.Exchange(ref _noticeCallCount, 0);
            Interlocked.Exchange(ref _revertOfferCallCount, 0);
        }
    }
}
