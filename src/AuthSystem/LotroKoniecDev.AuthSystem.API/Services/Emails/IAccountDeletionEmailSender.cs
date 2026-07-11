using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

internal interface IAccountDeletionEmailSender
{
    Task<Result> SendDeletionScheduledEmailAsync(
        string email,
        string cancelToken,
        DateTimeOffset finalizesAt,
        CancellationToken cancellationToken);

    Task<Result> SendDeletionCancelledEmailAsync(
        string email,
        CancellationToken cancellationToken);
}
