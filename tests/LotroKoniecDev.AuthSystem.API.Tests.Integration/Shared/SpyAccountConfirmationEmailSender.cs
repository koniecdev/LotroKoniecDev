using LotroKoniecDev.AuthSystem.API.Services.Emails;
using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;

#pragma warning disable CA1515
public sealed class SpyAccountConfirmationEmailSender : IAccountConfirmationEmailSender
#pragma warning restore CA1515
{
    private int _callCount;
    private int _registrationAttemptedCallCount;

    public Guid? LastUserId { get; private set; }
    public string? LastEmail { get; private set; }
    public string? LastConfirmationToken { get; private set; }
    public int CallCount => Volatile.Read(ref _callCount);
    public string? LastRegistrationAttemptedRecipient { get; private set; }
    public int RegistrationAttemptedCallCount => Volatile.Read(ref _registrationAttemptedCallCount);
    public bool ShouldFail { get; set; }

    public Task<Result> SendEmailConfirmationAsync(Guid userId, string email, string confirmationToken, CancellationToken cancellationToken)
    {
        LastUserId = userId;
        LastEmail = email;
        LastConfirmationToken = confirmationToken;
        Interlocked.Increment(ref _callCount);

        return ShouldFail
            ? Task.FromResult(Result.Failure(new Error("Test.EmailFailed", "Simulated email failure")))
            : Task.FromResult(Result.Success());
    }

    public Task<Result> SendRegistrationAttemptedNoticeAsync(Guid userId, string email, CancellationToken cancellationToken)
    {
        LastRegistrationAttemptedRecipient = email;
        Interlocked.Increment(ref _registrationAttemptedCallCount);

        return Task.FromResult(Result.Success());
    }

    /// <summary>
    /// Blocks until a confirmation e-mail lands or the timeout passes. Registration stopped being
    /// synchronous when it went through the outbox (commit -> relay -> delivery -> this spy), so
    /// tests reading <see cref="LastEmail"/> right after a register call must wait on state, not
    /// assume immediacy. It returns either way, and the assertions stay in the test.
    /// </summary>
    public async Task WaitForCaptureAsync(TimeSpan? timeout = null)
    {
        using CancellationTokenSource waitWindow = new(timeout ?? TimeSpan.FromSeconds(15));

        while (LastEmail is null && !waitWindow.IsCancellationRequested)
        {
            await Task.Delay(50);
        }
    }

    /// <summary>
    /// Blocks until the notice to the owner of a taken address lands, or the timeout passes. Like the
    /// confirmation, it travels the outbox pipeline (#860).
    /// </summary>
    public async Task WaitForRegistrationAttemptedCaptureAsync(TimeSpan? timeout = null)
    {
        using CancellationTokenSource waitWindow = new(timeout ?? TimeSpan.FromSeconds(15));

        while (RegistrationAttemptedCallCount == 0 && !waitWindow.IsCancellationRequested)
        {
            await Task.Delay(50);
        }
    }

    public void Reset()
    {
        LastUserId = null;
        LastEmail = null;
        LastConfirmationToken = null;
        Interlocked.Exchange(ref _callCount, 0);
        LastRegistrationAttemptedRecipient = null;
        Interlocked.Exchange(ref _registrationAttemptedCallCount, 0);
        ShouldFail = false;
    }
}
