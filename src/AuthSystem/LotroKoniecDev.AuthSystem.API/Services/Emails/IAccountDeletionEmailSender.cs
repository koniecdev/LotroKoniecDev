using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

internal interface IAccountDeletionEmailSender
{
    Task<Result> SendDeletionScheduledEmailAsync(
        Guid userId,
        string email,
        string cancelToken,
        DateTimeOffset finalizesAt,
        CancellationToken cancellationToken);

    Task<Result> SendDeletionCancelledEmailAsync(
        Guid userId,
        string email,
        CancellationToken cancellationToken);
}
