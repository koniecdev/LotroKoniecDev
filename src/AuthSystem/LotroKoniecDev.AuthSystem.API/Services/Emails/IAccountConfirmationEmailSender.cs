using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

internal interface IAccountConfirmationEmailSender
{
    Task<Result> SendEmailConfirmationAsync(
        Guid userId,
        string email,
        string confirmationToken,
        CancellationToken cancellationToken);

    /// <summary>
    /// To the owner of an address someone just tried to register with. The register form answers the same
    /// for a free and a taken address (#860), so this is how the owner learns about the attempt.
    /// </summary>
    Task<Result> SendRegistrationAttemptedNoticeAsync(
        Guid userId,
        string email,
        CancellationToken cancellationToken);
}
