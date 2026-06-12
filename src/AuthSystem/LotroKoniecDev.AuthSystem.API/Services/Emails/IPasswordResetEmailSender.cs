using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

internal interface IPasswordResetEmailSender
{
    Task<Result> SendPasswordResetEmailAsync(string email, string resetToken, CancellationToken cancellationToken);
}
