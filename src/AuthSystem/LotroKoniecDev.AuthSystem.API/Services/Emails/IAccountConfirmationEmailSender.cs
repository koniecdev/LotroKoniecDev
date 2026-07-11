using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

internal interface IAccountConfirmationEmailSender
{
    Task<Result> SendEmailConfirmationAsync(
        Guid userId,
        string email,
        string confirmationToken,
        CancellationToken cancellationToken);
}
