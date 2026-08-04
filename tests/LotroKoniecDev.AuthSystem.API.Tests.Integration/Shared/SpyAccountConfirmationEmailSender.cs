using LotroKoniecDev.AuthSystem.API.Services.Emails;
using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;

#pragma warning disable CA1515
public sealed class SpyAccountConfirmationEmailSender : IAccountConfirmationEmailSender
#pragma warning restore CA1515
{
    private int _callCount;

    public string? LastEmail { get; private set; }
    public string? LastConfirmationToken { get; private set; }
    public int CallCount => Volatile.Read(ref _callCount);
    public bool ShouldFail { get; set; }

    public Task<Result> SendEmailConfirmationAsync(Guid userId, string email, string confirmationToken, CancellationToken cancellationToken)
    {
        LastEmail = email;
        LastConfirmationToken = confirmationToken;
        Interlocked.Increment(ref _callCount);

        return ShouldFail
            ? Task.FromResult(Result.Failure(new Error("Test.EmailFailed", "Simulated email failure")))
            : Task.FromResult(Result.Success());
    }

    /// <summary>
    /// Blocks until a confirmation e-mail lands or the timeout passes. Registration stopped being
    /// synchronous when it went through the outbox (commit -> relay -> delivery -> this spy), so
    /// tests reading <see cref="LastEmail"/> right after a register call must wait on state, not
    /// assume immediacy. Returns silently either way — the assertions stay at the call site.
    /// </summary>
    public async Task WaitForCaptureAsync(TimeSpan? timeout = null)
    {
        using CancellationTokenSource waitWindow = new(timeout ?? TimeSpan.FromSeconds(15));

        while (LastEmail is null && !waitWindow.IsCancellationRequested)
        {
            await Task.Delay(50);
        }
    }

    public void Reset()
    {
        LastEmail = null;
        LastConfirmationToken = null;
        Interlocked.Exchange(ref _callCount, 0);
        ShouldFail = false;
    }
}
