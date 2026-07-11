namespace LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;

public sealed record CancelAccountDeletionRequest(
    string Email,
    string Token);
