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
}
