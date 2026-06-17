using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LotroKoniecDev.TranslationSystem.E2E.Tests.Extensions;

internal static class HttpResponseMessageExtensions
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Returns the response body on success; on failure throws an <see cref="HttpRequestException"/>
    /// whose message unpacks the RFC-7807 problem details, so a failing E2E call reports the API's
    /// own error instead of a bare status code.
    /// </summary>
    public static async Task<string> EnsureSuccessWithDetailsAsync(this HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.NoContent)
        {
            return string.Empty;
        }

        string content = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode)
        {
            return content;
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new HttpRequestException(
                $"Response status code does not indicate success: {(int)response.StatusCode} ({response.StatusCode}). " +
                $"Response body was empty. RequestUri: {response.RequestMessage?.RequestUri}");
        }

        ProblemDetailsDto? problemDetails = JsonSerializer.Deserialize<ProblemDetailsDto>(content, JsonOptions);

        if (problemDetails is not null && !string.IsNullOrEmpty(problemDetails.Title))
        {
            string errorMessage =
                $"Response status code does not indicate success: {(int)response.StatusCode} ({response.StatusCode}).\n" +
                $"Title: {problemDetails.Title}\n" +
                $"Detail: {problemDetails.Detail}\n" +
                $"Type: {problemDetails.Type}\n";

            if (!(problemDetails.Extensions?.Count > 0))
            {
                throw new HttpRequestException(errorMessage);
            }

            errorMessage += "Extensions:\n";
            foreach (KeyValuePair<string, JsonElement> extension in problemDetails.Extensions)
            {
                errorMessage += $"  {extension.Key}: {extension.Value}\n";
            }

            throw new HttpRequestException(errorMessage);
        }

        throw new HttpRequestException(
            $"Response status code does not indicate success: {(int)response.StatusCode} ({response.StatusCode}).\n" +
            $"Content: {content}");
    }

    [SuppressMessage("Major Code Smell", "S3459:Unassigned members should be removed",
        Justification = "Deserialization DTO — properties are assigned by System.Text.Json.")]
    private sealed class ProblemDetailsDto
    {
        public string? Type { get; init; }
        public string? Title { get; init; }
        public int? Status { get; init; }
        public string? Detail { get; init; }
        public string? Instance { get; init; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extensions { get; init; }
    }
}
