using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

/// <summary>
/// The four messages of the e-mail change flow. Two go to the old mailbox, and they are what makes a
/// stolen password recoverable (ADR-0048), so neither is optional.
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
