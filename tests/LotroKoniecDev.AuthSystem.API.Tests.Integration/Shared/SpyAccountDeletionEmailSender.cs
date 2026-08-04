using LotroKoniecDev.AuthSystem.API.Services.Emails;
using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;

#pragma warning disable CA1515
public sealed class SpyAccountDeletionEmailSender : IAccountDeletionEmailSender
#pragma warning restore CA1515
{
    private int _scheduledCallCount;
    private int _cancelledCallCount;

    public string? LastScheduledEmail { get; private set; }
    public string? LastCancelToken { get; private set; }
    public DateTimeOffset? LastFinalizesAt { get; private set; }
    public string? LastCancelledEmail { get; private set; }
    public int ScheduledCallCount => Volatile.Read(ref _scheduledCallCount);
    public int CancelledCallCount => Volatile.Read(ref _cancelledCallCount);
    public bool ShouldFailScheduledEmail { get; set; }

    public Task<Result> SendDeletionScheduledEmailAsync(
        Guid userId,
        string email,
        string cancelToken,
        DateTimeOffset finalizesAt,
        CancellationToken cancellationToken)
    {
        LastScheduledEmail = email;
        LastCancelToken = cancelToken;
        LastFinalizesAt = finalizesAt;
        Interlocked.Increment(ref _scheduledCallCount);

        return ShouldFailScheduledEmail
            ? Task.FromResult(Result.Failure(new Error("Test.EmailFailed", "Simulated email failure")))
            : Task.FromResult(Result.Success());
    }

    public Task<Result> SendDeletionCancelledEmailAsync(Guid userId, string email, CancellationToken cancellationToken)
    {
        LastCancelledEmail = email;
        Interlocked.Increment(ref _cancelledCallCount);
        return Task.FromResult(Result.Success());
    }

    /// <summary>
    /// Blocks until a deletion-scheduled e-mail lands or the timeout passes. Scheduling stopped
    /// being synchronous when the e-mail went through the outbox (commit -> relay -> delivery ->
    /// this spy), so tests reading <see cref="LastCancelToken"/> right after the delete call must
    /// wait on state, not assume immediacy. Returns silently either way — the assertions stay at
    /// the call site.
    /// </summary>
    public async Task WaitForScheduledCaptureAsync(TimeSpan? timeout = null)
    {
        using CancellationTokenSource waitWindow = new(timeout ?? TimeSpan.FromSeconds(15));

        while (LastCancelToken is null && !waitWindow.IsCancellationRequested)
        {
            await Task.Delay(50);
        }
    }

    /// <summary>
    /// Blocks until a deletion-cancelled e-mail lands or the timeout passes — the courtesy
    /// notice travels the same asynchronous pipeline as the scheduled e-mail.
    /// </summary>
    public async Task WaitForCancelledCaptureAsync(TimeSpan? timeout = null)
    {
        using CancellationTokenSource waitWindow = new(timeout ?? TimeSpan.FromSeconds(15));

        while (LastCancelledEmail is null && !waitWindow.IsCancellationRequested)
        {
            await Task.Delay(50);
        }
    }

    public void Reset()
    {
        LastScheduledEmail = null;
        LastCancelToken = null;
        LastFinalizesAt = null;
        LastCancelledEmail = null;
        Interlocked.Exchange(ref _scheduledCallCount, 0);
        Interlocked.Exchange(ref _cancelledCallCount, 0);
        ShouldFailScheduledEmail = false;
    }
}
