using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.Infrastructure.Emails;

public interface IEmailService
{
    Task<Result> SendAsync(
        string receiverEmail,
        string subject,
        EmailBody body,
        CancellationToken cancellationToken = default);
}
