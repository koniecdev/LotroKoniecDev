using System.Text.Json.Serialization;

namespace LotroKoniecDev.TranslationSystem.E2E.Tests.Clients.Responses;

/// <summary>
/// The OAuth2 token endpoint response (<c>connect/token</c>) shape, mapped from the
/// snake_case OpenIddict JSON payload.
/// </summary>
public sealed record TokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("scope")] string Scope);
