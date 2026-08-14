using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LotroKoniecDev.Frontend.Infrastructure.Errors;
using Microsoft.AspNetCore.Mvc;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace LotroKoniecDev.Frontend.Infrastructure.HttpClients;

/// <summary>
/// Turns raw <see cref="HttpClient"/> calls into <see cref="ApiResult"/>/<see cref="ApiResult{T}"/>:
/// success deserializes the body, an error status deserializes the API's <c>ProblemDetails</c>, and a
/// transport failure (no HTTP status — Polly timeout/circuit-open, socket error) is mapped to a Polish
/// <c>ProblemDetails</c> so the SSR page renders a message instead of crashing. The Frontend is
/// Polish-only, so these messages are inlined rather than localized.
/// </summary>
internal static class HttpClientApiExtensions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    extension(HttpClient httpClient)
    {
        public async Task<ApiResult<T>> GetApiResultAsync<T>(
            string uri,
            CancellationToken cancellationToken = default)
        {
            using HttpRequestMessage request = new(
                HttpMethod.Get,
                new Uri(uri, UriKind.RelativeOrAbsolute));
            return await SendForApiResultAsync<T>(httpClient, request, cancellationToken);
        }

        public async Task<ApiResult<string>> GetTextAsync(
            string uri,
            CancellationToken cancellationToken = default)
        {
            using HttpRequestMessage request = new(
                HttpMethod.Get,
                new Uri(uri, UriKind.RelativeOrAbsolute));
            try
            {
                using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
                string content = await response.Content.ReadAsStringAsync(cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    return ApiResult.Success(content);
                }

                return ApiResult.Failure<string>(ParseProblemDetails(content, response));
            }
            catch (Exception ex) when (IsTransportFailure(ex))
            {
                return ApiResult.Failure<string>(MapTransportFailureToProblemDetails(ex));
            }
        }

        public async Task<ApiResult> PostApiResultAsync(
            string uri,
            object body,
            CancellationToken cancellationToken = default)
        {
            using HttpRequestMessage request = new(HttpMethod.Post, new Uri(uri, UriKind.RelativeOrAbsolute));
            request.Content = JsonContent.Create(body, body.GetType(), options: JsonOptions);
            return await SendForApiResultAsync(httpClient, request, cancellationToken);
        }

        public async Task<ApiResult<T>> PostApiResultAsync<T>(
            string uri,
            object body,
            CancellationToken cancellationToken = default)
        {
            using HttpRequestMessage request = new(HttpMethod.Post, new Uri(uri, UriKind.RelativeOrAbsolute));
            request.Content = JsonContent.Create(body, body.GetType(), options: JsonOptions);
            return await SendForApiResultAsync<T>(httpClient, request, cancellationToken);
        }

        /// <summary>
        /// For endpoints whose success payload travels in response headers rather than a body
        /// (e.g. <c>204</c> + <c>X-Deletion-Finalizes-At</c> on account-deletion scheduling). Behaves
        /// exactly like the body-less <c>PostApiResultAsync</c> on failure — a <c>ProblemDetails</c>
        /// keeps the monad path intact.
        /// </summary>
        public async Task<ApiResult<ApiResponseHeaders>> PostForHeadersApiResultAsync(
            string uri,
            object body,
            CancellationToken cancellationToken = default)
        {
            using HttpRequestMessage request = new(HttpMethod.Post, new Uri(uri, UriKind.RelativeOrAbsolute));
            request.Content = JsonContent.Create(body, body.GetType(), options: JsonOptions);
            try
            {
                using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    return ApiResult.Success(ApiResponseHeaders.From(response.Headers));
                }

                string content = await response.Content.ReadAsStringAsync(cancellationToken);
                return ApiResult.Failure<ApiResponseHeaders>(ParseProblemDetails(content, response));
            }
            catch (Exception ex) when (IsTransportFailure(ex))
            {
                return ApiResult.Failure<ApiResponseHeaders>(MapTransportFailureToProblemDetails(ex));
            }
        }

        public async Task<ApiResult> PutApiResultAsync(
            string uri,
            object body,
            CancellationToken cancellationToken = default)
        {
            using HttpRequestMessage request = new(HttpMethod.Put, new Uri(uri, UriKind.RelativeOrAbsolute));
            request.Content = JsonContent.Create(body, body.GetType(), options: JsonOptions);
            return await SendForApiResultAsync(httpClient, request, cancellationToken);
        }

        public async Task<ApiResult<T>> PutApiResultAsync<T>(
            string uri,
            object body,
            CancellationToken cancellationToken = default)
        {
            using HttpRequestMessage request = new(HttpMethod.Put, new Uri(uri, UriKind.RelativeOrAbsolute));
            request.Content = JsonContent.Create(body, body.GetType(), options: JsonOptions);
            return await SendForApiResultAsync<T>(httpClient, request, cancellationToken);
        }

        public async Task<ApiResult> PutApiResultAsync(
            string uri,
            CancellationToken cancellationToken = default)
        {
            using HttpRequestMessage request = new(HttpMethod.Put, new Uri(uri, UriKind.RelativeOrAbsolute));
            return await SendForApiResultAsync(httpClient, request, cancellationToken);
        }

        public async Task<ApiResult> PatchApiResultAsync(
            string uri,
            object body,
            CancellationToken cancellationToken = default)
        {
            using HttpRequestMessage request = new(HttpMethod.Patch, new Uri(uri, UriKind.RelativeOrAbsolute));
            request.Content = JsonContent.Create(body, body.GetType(), options: JsonOptions);
            return await SendForApiResultAsync(httpClient, request, cancellationToken);
        }

        public async Task<ApiResult> DeleteApiResultAsync(
            string uri,
            CancellationToken cancellationToken = default)
        {
            using HttpRequestMessage request = new(HttpMethod.Delete, new Uri(uri, UriKind.RelativeOrAbsolute));
            return await SendForApiResultAsync(httpClient, request, cancellationToken);
        }

        public async Task<ApiResult> SendMultipartApiResultAsync(
            HttpMethod method,
            string uri,
            MultipartFormDataContent content,
            CancellationToken cancellationToken = default)
        {
            using HttpRequestMessage request = new(method, new Uri(uri, UriKind.RelativeOrAbsolute))
            {
                Content = content
            };
            return await SendForApiResultAsync(httpClient, request, cancellationToken);
        }

        public async Task<ApiResult<T>> SendMultipartApiResultAsync<T>(
            HttpMethod method,
            string uri,
            MultipartFormDataContent content,
            CancellationToken cancellationToken = default)
        {
            using HttpRequestMessage request = new(method, new Uri(uri, UriKind.RelativeOrAbsolute))
            {
                Content = content
            };
            return await SendForApiResultAsync<T>(httpClient, request, cancellationToken);
        }
    }

    private static async Task<ApiResult<T>> SendForApiResultAsync<T>(
        HttpClient httpClient,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
            string content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                // A success with no body (e.g. 204 No Content) has nothing to deserialize — surface the
                // default value rather than throwing a JsonException on an empty string.
                if (string.IsNullOrWhiteSpace(content))
                {
                    return ApiResult.Success<T>(default!);
                }

                T value = JsonSerializer.Deserialize<T>(content, JsonOptions)
                          ?? throw new JsonException($"Failed to deserialize {typeof(T).Name}");
                return ApiResult.Success(value);
            }

            return ApiResult.Failure<T>(ParseProblemDetails(content, response));
        }
        catch (Exception ex) when (IsTransportFailure(ex))
        {
            return ApiResult.Failure<T>(MapTransportFailureToProblemDetails(ex));
        }
    }

    private static async Task<ApiResult> SendForApiResultAsync(
        HttpClient httpClient,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return ApiResult.Success();
            }

            string content = await response.Content.ReadAsStringAsync(cancellationToken);
            return ApiResult.Failure(ParseProblemDetails(content, response));
        }
        catch (Exception ex) when (IsTransportFailure(ex))
        {
            return ApiResult.Failure(MapTransportFailureToProblemDetails(ex));
        }
    }

    /// <summary>
    /// Deserializes the API's error body into <see cref="ProblemDetails"/>. Not every error carries
    /// one — a bare <c>401</c> from the JWT bearer challenge has an empty body, and a stopped
    /// upstream answers through the reverse proxy with the proxy's own HTML — so an empty or
    /// malformed body is synthesized into a bare <see cref="ProblemDetails"/> that still carries
    /// the real status code, keeping classifications like <c>IsUnauthorized</c> intact instead of
    /// crashing the SSR page on a <see cref="JsonException"/>.
    /// </summary>
    private static ProblemDetails ParseProblemDetails(string content, HttpResponseMessage response)
    {
        if (!string.IsNullOrWhiteSpace(content))
        {
            try
            {
                ProblemDetails? problem = JsonSerializer.Deserialize<ProblemDetails>(content, JsonOptions);
                if (problem is not null)
                {
                    problem.Status ??= (int)response.StatusCode;

                    // Whatever the body claims, it came off the wire and is English until proven
                    // otherwise — it does not get to wear the Frontend-authored marker (ADR-0044 §2).
                    ApiProblemCopy.StripFrontendAuthoredMarker(problem);
                    return problem;
                }
            }
            catch (JsonException)
            {
                // Fall through to the synthesized ProblemDetails below.
            }
        }

        return ApiProblemCopy.StatusOnly((int)response.StatusCode);
    }

    /// <summary>
    /// The resilience pipeline (Polly) can throw before we ever see an HTTP status code.
    /// These are translated into a <see cref="ProblemDetails"/> so callers stay on the
    /// <c>ApiResult</c> path and can render a message instead of crashing the page.
    /// </summary>
    private static bool IsTransportFailure(Exception ex) =>
        ex is HttpRequestException
            or TaskCanceledException
            or BrokenCircuitException
            or TimeoutRejectedException;

    private static ProblemDetails MapTransportFailureToProblemDetails(Exception ex) => ex switch
    {
        BrokenCircuitException => ApiProblemCopy.FrontendAuthored(
            "Usługa chwilowo niedostępna",
            "Połączenie z serwerem zostało tymczasowo wstrzymane. Spróbuj ponownie za chwilę.",
            StatusCodes.Status503ServiceUnavailable),
        TimeoutRejectedException or TaskCanceledException => ApiProblemCopy.FrontendAuthored(
            "Przekroczono czas oczekiwania",
            "Serwer nie odpowiedział w wyznaczonym czasie. Spróbuj ponownie.",
            StatusCodes.Status504GatewayTimeout),
        _ => ApiProblemCopy.FrontendAuthored(
            "Błąd połączenia",
            "Nie udało się połączyć z serwerem. Spróbuj ponownie za chwilę.",
            StatusCodes.Status503ServiceUnavailable)
    };
}
