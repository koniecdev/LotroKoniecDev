using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

/// <summary>
/// The messages of the e-mail change flow. Two go to the old mailbox, and they are what makes a stolen
/// password recoverable (ADR-0048), so neither is optional. One goes to the owner of a new address that
/// already has an account, in place of the link (#860).
/// </summary>
internal interface IEmailChangeEmailSender
{
    /// <summary>To the new address: the link that applies the change.</summary>
    Task<Result> SendVerificationAsync(
        Guid userId,
        string newEmail,
        string verificationToken,
        CancellationToken cancellationToken);

    /// <summary>
    /// To the owner of an account at the new address, instead of the link: somebody asked to move another
    /// account to this address, and nothing changed (#860).
    /// </summary>
    Task<Result> SendAddressTakenNoticeAsync(
        Guid ownerUserId,
        string ownerEmail,
        CancellationToken cancellationToken);

    /// <summary>
    /// To the old address, while the change is still pending: somebody asked to move this account.
    /// </summary>
    Task<Result> SendChangeRequestedWarningAsync(
        Guid userId,
        string currentEmail,
        string newEmail,
        CancellationToken cancellationToken);

    /// <summary>To the new address, after the change: this is now your login.</summary>
    Task<Result> SendChangedNoticeAsync(
        Guid userId,
        string newEmail,
        string previousEmail,
        CancellationToken cancellationToken);

    /// <summary>
    /// To the old address, after the change: it happened, and here is the link that undoes it.
    /// </summary>
    Task<Result> SendChangedNoticeWithRevertAsync(
        Guid userId,
        string previousEmail,
        string newEmail,
        string revertToken,
        TimeSpan revertWindow,
        CancellationToken cancellationToken);
}
