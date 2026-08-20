using LotroKoniecDev.AuthSystem.API.Services.Emails;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;

#pragma warning disable CA1515
public sealed class SpyPasswordResetEmailSender : IPasswordResetEmailSender
#pragma warning restore CA1515
{
    private int _callCount;

    public string? LastEmail { get; private set; }
    public string? LastResetToken { get; private set; }
    public int CallCount => Volatile.Read(ref _callCount);

    public Task<Result> SendPasswordResetEmailAsync(Guid userId, string email, string resetToken, CancellationToken cancellationToken)
    {
        LastEmail = email;
        LastResetToken = resetToken;
        Interlocked.Increment(ref _callCount);
        return Task.FromResult(Result.Success());
    }

    /// <summary>
    /// Blocks until a reset e-mail lands or the timeout passes. Forgot-password stopped being
    /// synchronous when it went through the outbox (commit -> relay -> delivery -> this spy), so
    /// tests reading <see cref="LastResetToken"/> right after the forgot-password call must wait
    /// for the state instead of assuming it is already there. It returns either way, and the
    /// assertions stay in the test.
    /// </summary>
    public async Task WaitForCaptureAsync(TimeSpan? timeout = null)
    {
        using CancellationTokenSource waitWindow = new(timeout ?? TimeSpan.FromSeconds(15));

        while (LastResetToken is null && !waitWindow.IsCancellationRequested)
        {
            await Task.Delay(50);
        }
    }

    public void Reset()
    {
        LastEmail = null;
        LastResetToken = null;
        Interlocked.Exchange(ref _callCount, 0);
    }
}
