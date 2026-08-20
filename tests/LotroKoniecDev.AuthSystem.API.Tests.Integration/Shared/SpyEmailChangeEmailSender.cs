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
        WaitForAsync(() => LastVerificationToken is not null, timeout);

    public Task WaitForRevertOfferCaptureAsync(TimeSpan? timeout = null) =>
        WaitForAsync(() => LastRevertToken is not null, timeout);

    /// <summary>
    /// Waits for the notice sent to the new address. A change that arms no undo link sends only this
    /// one, so it is the signal that the dispatch finished at all.
    /// </summary>
    public Task WaitForChangedNoticeCaptureAsync(TimeSpan? timeout = null) =>
        WaitForAsync(() => LastNoticeRecipient is not null, timeout);

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
