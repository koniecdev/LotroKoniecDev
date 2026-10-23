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
    /// Waits until an e-mail lands, and throws when the time runs out. A test creates its own instance
    /// for one host, so no other test's mail can reach it, and it keeps only the last body.
    /// </summary>
    public Task WaitForCaptureAsync(TimeSpan? timeout = null) =>
        MailWait.UntilAsync(() => LastBody is not null, "An e-mail", timeout);
}
