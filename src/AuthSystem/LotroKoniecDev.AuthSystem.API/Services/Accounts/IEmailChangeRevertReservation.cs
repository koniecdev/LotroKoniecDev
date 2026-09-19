namespace LotroKoniecDev.AuthSystem.API.Services.Accounts;

/// <summary>
/// Answers whether an address is still held for an account that can undo an e-mail change onto it
/// (#684). An address freed by a change is not free while its owner still holds a working revert
/// link, because taking it is what would make that link fail forever.
/// </summary>
internal interface IEmailChangeRevertReservation
{
    /// <param name="email">The address somebody wants to take, in the form the caller typed it.</param>
    /// <param name="exceptUserId">
    /// The account making the request, if there is one. Its own armed address never blocks it —
    /// moving back to where the chain started is the one thing this reservation exists to protect.
    /// </param>
    Task<bool> IsReservedAsync(string email, Guid? exceptUserId, CancellationToken cancellationToken);
}
