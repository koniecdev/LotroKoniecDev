using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.Infrastructure.Emails;

public interface IEmailService
{
    Task<Result> SendAsync(
        string receiverEmail,
        string subject,
        string body,
        bool isBodyHtml = true,
        CancellationToken cancellationToken = default);
}
