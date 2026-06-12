namespace LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Password;

public sealed record ResetPasswordRequest(
    string Email,
    string Token,
    string NewPassword);
