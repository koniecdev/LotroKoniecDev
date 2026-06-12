namespace LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;

public sealed record RegisterRequest(
    string Username,
    string Email,
    string Password,
    string PhoneNumber,
    bool AcceptedPrivacyPolicy,
    bool AcceptedDataProcessingConsent);
