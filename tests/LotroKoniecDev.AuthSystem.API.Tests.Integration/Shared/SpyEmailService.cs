using LotroKoniecDev.AuthSystem.Infrastructure.Emails;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;

#pragma warning disable CA1515
public sealed class SpyEmailService : IEmailService
#pragma warning restore CA1515
{
    public EmailBody? LastBody { get; private set; }

    public Task<Result> SendAsync(
        string receiverEmail,
        string subject,
        EmailBody body,
        CancellationToken cancellationToken = default)
    {
        LastBody = body;
        return Task.FromResult(Result.Success());
    }

    /// <summary>
    /// Blocks until an e-mail lands or the timeout passes. E-mails leave the request path through
    /// the outbox (commit -> relay -> delivery -> this spy, ADR-0038), so tests reading
    /// <see cref="LastBody"/> right after the triggering call must wait on state, not assume
    /// immediacy. Returns silently either way — the assertions stay at the call site.
    /// </summary>
    public async Task WaitForCaptureAsync(TimeSpan? timeout = null)
    {
        using CancellationTokenSource waitWindow = new(timeout ?? TimeSpan.FromSeconds(15));

        while (LastBody is null && !waitWindow.IsCancellationRequested)
        {
            await Task.Delay(50);
        }
    }
}
