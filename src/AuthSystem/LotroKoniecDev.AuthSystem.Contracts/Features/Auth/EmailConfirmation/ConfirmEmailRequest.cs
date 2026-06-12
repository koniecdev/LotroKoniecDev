namespace LotroKoniecDev.AuthSystem.Contracts.Features.Auth.EmailConfirmation;

public sealed record ConfirmEmailRequest(string Email, string Token);
