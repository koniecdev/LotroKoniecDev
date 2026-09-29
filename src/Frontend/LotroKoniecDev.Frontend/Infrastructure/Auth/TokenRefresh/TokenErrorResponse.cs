using System.Text.Json.Serialization;

namespace LotroKoniecDev.Frontend.Infrastructure.Auth.TokenRefresh;

/// <summary>
/// The OAuth error body the auth API sends when it refuses a token request (RFC 6749 §5.2, #903).
/// </summary>
internal sealed class TokenErrorResponse
{
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; init; }
}
