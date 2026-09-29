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
        LastVerificationRecipient = newEmail;
        LastVerificationToken = verificationToken;
        Interlocked.Increment(ref _verificationCallCount);
        return Task.FromResult(Result.Success());
    }

    public Task<Result> SendChangeRequestedWarningAsync(
        Guid userId, string currentEmail, string newEmail, CancellationToken cancellationToken)
    {
        LastWarningRecipient = currentEmail;
        LastWarningTargetAddress = newEmail;
        Interlocked.Increment(ref _warningCallCount);
        return Task.FromResult(Result.Success());
    }

    public Task<Result> SendChangedNoticeAsync(
        Guid userId, string newEmail, string previousEmail, CancellationToken cancellationToken)
    {
        LastNoticeRecipient = newEmail;
        Interlocked.Increment(ref _noticeCallCount);
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
        LastRevertOfferRecipient = previousEmail;
        LastRevertOfferTargetAddress = newEmail;
        LastRevertToken = revertToken;
        Interlocked.Increment(ref _revertOfferCallCount);
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

    public Task WaitForRevertOfferCaptureAsync(TimeSpan? timeout = null) =>
        WaitForAsync(() => RevertOfferCallCount > 0, timeout);

    /// <summary>
    /// Waits for the notice sent to the new address. It is the last mail a confirmed change sends:
    /// after the undo link when the change arms one, and on its own when it does not. So once it is
    /// here, every mail of that change is here too (#772). A helper that completes a change must wait
    /// for it. If it does not, a mail of that change can land after the next <see cref="Reset"/> and
    /// pass for the next test's own (#950).
    /// </summary>
    public Task WaitForChangedNoticeCaptureAsync(TimeSpan? timeout = null) =>
        WaitForAsync(() => NoticeCallCount > 0, timeout);

    /// <summary>
    /// Each wait checks the counter of its mail, not a field. The spy bumps the counter last, with
    /// <see cref="Interlocked.Increment(ref int)"/>, so every field of that mail is set once the wait
    /// sees it. A wait that runs out returns quietly, so a test that then checks for a missing mail must
    /// first check that the mail it waited for is there. Throwing instead needs #950 first: a mail left
    /// over from the previous test would make the full run fail about every second time.
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
