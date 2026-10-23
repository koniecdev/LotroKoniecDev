namespace LotroKoniecDev.AuthSystem.API.Services.Accounts;

/// <summary>
/// An address freed by an e-mail change stays held while its owner holds a working revert link, because
/// taking it would make that link fail forever (#684).
/// </summary>
internal interface IEmailChangeRevertReservation
{
    /// <param name="email">As the caller typed it.</param>
    /// <param name="exceptUserId">
    /// The requesting account, if any. Its own armed address never blocks it: moving back there is what
    /// the reservation protects.
    /// </param>
    Task<bool> IsReservedAsync(string email, Guid? exceptUserId, CancellationToken cancellationToken);
}
