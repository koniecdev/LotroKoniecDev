namespace LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Password;

public sealed record ChangePasswordRequest(
    string CurrentPassword,
    string NewPassword);
