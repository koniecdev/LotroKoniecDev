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

    public Task<Result> SendEmailConfirmationAsync(string email, string confirmationToken, CancellationToken cancellationToken)
    {
        LastEmail = email;
        LastConfirmationToken = confirmationToken;
        Interlocked.Increment(ref _callCount);

        return ShouldFail
            ? Task.FromResult(Result.Failure(new Error("Test.EmailFailed", "Simulated email failure")))
            : Task.FromResult(Result.Success());
    }

    public void Reset()
    {
        LastEmail = null;
        LastConfirmationToken = null;
        Interlocked.Exchange(ref _callCount, 0);
        ShouldFail = false;
    }
}
