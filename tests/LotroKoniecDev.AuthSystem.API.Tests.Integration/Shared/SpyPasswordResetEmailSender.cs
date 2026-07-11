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

    public void Reset()
    {
        LastEmail = null;
        LastResetToken = null;
        Interlocked.Exchange(ref _callCount, 0);
    }
}
