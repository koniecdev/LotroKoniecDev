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
    /// The same cancel link, sent to the address an armed undo would restore (#685). It exists because
    /// after an e-mail change the ordinary notice above reaches whoever moved the account, and that
    /// may be the attacker rather than the owner.
    /// </summary>
    /// <param name="previousEmail">The armed address, which is who reads this message.</param>
    /// <param name="currentEmail">
    /// The address the account sits on now. It is named in the text so the reader knows where their
    /// account went, and it is what the link carries: <c>CancelAccountDeletion</c> finds the account
    /// with it, so a link rewritten to the old address would verify against nothing.
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
