using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

internal interface IAccountConfirmationEmailSender
{
    Task<Result> SendEmailConfirmationAsync(
        string email,
        string confirmationToken,
        CancellationToken cancellationToken);
}
