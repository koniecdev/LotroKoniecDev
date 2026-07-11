namespace LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;

public sealed record RegisterRequest(
    string Username,
    string Email,
    string Password,
    bool AcceptedPrivacyPolicy,
    bool AcceptedDataProcessingConsent,
    bool AcceptedTermsOfService);
