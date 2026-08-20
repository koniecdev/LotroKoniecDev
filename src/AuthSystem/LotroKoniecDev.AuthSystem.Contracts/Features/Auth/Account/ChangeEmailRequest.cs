namespace LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;

public sealed record ChangeEmailRequest(string NewEmail, string CurrentPassword);
