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

    /// <summary>
    /// The same cancel link, sent to the address an armed undo would restore (#685), because after an
    /// e-mail change the ordinary notice may reach the attacker instead of the owner.
    /// </summary>
    /// <param name="previousEmail">The armed address, which reads this message.</param>
    /// <param name="currentEmail">
    /// The account's current address, named in the text and carried by the link, because
    /// <c>CancelAccountDeletion</c> finds the account with it.
    /// </param>
    Task<Result> SendDeletionScheduledNoticeToPreviousAddressAsync(
        Guid userId,
        string previousEmail,
        string currentEmail,
        string cancelToken,
        DateTimeOffset finalizesAt,
        CancellationToken cancellationToken);

    Task<Result> SendDeletionCancelledEmailAsync(
        Guid userId,
        string email,
        CancellationToken cancellationToken);
}
