namespace LotroKoniecDev.AuthSystem.API.Services.Gdpr;

internal interface IAccountDeletionFinalizer
{
    Task<int> FinalizeDueAccountsAsync(CancellationToken cancellationToken);
}
