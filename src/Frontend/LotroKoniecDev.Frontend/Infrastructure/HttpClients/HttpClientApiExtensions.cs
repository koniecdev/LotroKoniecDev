using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LotroKoniecDev.Frontend.Infrastructure.Errors;
using Microsoft.AspNetCore.Mvc;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace LotroKoniecDev.Frontend.Infrastructure.HttpClients;

/// <summary>
/// Turns raw <see cref="HttpClient"/> calls into <see cref="ApiResult"/> and
/// <see cref="ApiResult{T}"/>. On success it reads the body, on an error status it reads the API's
/// <c>ProblemDetails</c>, and a transport failure with no HTTP status, such as a Polly timeout, an open
/// circuit or a socket error, becomes a Polish <c>ProblemDetails</c> so the page shows a message instead
/// of crashing. The Frontend is Polish only, so these messages are written here.
/// Nothing here throws because of what came off the wire: a body that is not the API's answer, on an
/// error status or on a success one, is a failed result and never an exception escaping the render.
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
        /// For endpoints that return their data in response headers instead of a body, such as a
        /// <c>204</c> with <c>X-Deletion-Finalizes-At</c> when an account deletion is scheduled. On
        /// failure it behaves exactly like the body-less <c>PostApiResultAsync</c>: a
        /// <c>ProblemDetails</c> keeps the result path intact.
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
                return ParseSuccessBody<T>(content);
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
    /// Reads a success body into <typeparamref name="T"/>. A success status does not prove the body is
    /// the API's answer: a reverse proxy serves its maintenance page with a <c>200</c>, and an auth
    /// redirect can land an HTML login page on an API URL.
    /// So a body that does not read as <typeparamref name="T"/>, or reads as the literal <c>null</c>, or
    /// is empty (#653), is treated like an unreadable error body: it becomes a
    /// <see cref="ProblemDetails"/> with only a status, which the Polish text ladder answers (#637). It
    /// never becomes a <see cref="JsonException"/> escaping the render (#638), and never a <c>null</c>
    /// <see cref="ApiResult{T}.Value"/> for the caller to use.
    /// Every generic verb promises a value, so <c>204 No Content</c> is not handled here. The body-less
    /// <see cref="ApiResult"/> verbs and <c>PostForHeadersApiResultAsync</c> are where nothing was
    /// promised.
    /// It reports <c>502 Bad Gateway</c> rather than the status it arrived with: whatever the status line
    /// said, the response was not valid, <c>200</c> has no failure text, and a download route would
    /// otherwise answer the browser with a problem carrying a <c>200</c>.
    /// </summary>
    private static ApiResult<T> ParseSuccessBody<T>(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return ApiResult.Failure<T>(ApiProblemCopy.StatusOnly(StatusCodes.Status502BadGateway));
        }

        try
        {
            T? value = JsonSerializer.Deserialize<T>(content, JsonOptions);
            if (value is not null)
            {
                return ApiResult.Success(value);
            }
        }
        catch (JsonException)
        {
            // Continue to the ProblemDetails built below.
        }

        return ApiResult.Failure<T>(ApiProblemCopy.StatusOnly(StatusCodes.Status502BadGateway));
    }

    /// <summary>
    /// Reads the API's error body into <see cref="ProblemDetails"/>. Not every error has one: a plain
    /// <c>401</c> from the JWT bearer challenge has an empty body, and a service that is down answers
    /// through the reverse proxy with the proxy's own HTML.
    /// So an empty or malformed body becomes a bare <see cref="ProblemDetails"/> that still carries the
    /// real status code. Checks such as <c>IsUnauthorized</c> keep working, and the page does not crash
    /// on a <see cref="JsonException"/>.
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

                    // Whatever the body says, it came off the wire and counts as English. It does not get
                    // to carry the Frontend-authored marker (ADR-0044 §2).
                    ApiProblemCopy.StripFrontendAuthoredMarker(problem);
                    return problem;
                }
            }
            catch (JsonException)
            {
                // Continue to the ProblemDetails built below.
            }
        }

        return ApiProblemCopy.StatusOnly((int)response.StatusCode);
    }

    /// <summary>
    /// The resilience pipeline (Polly) can throw before we ever see an HTTP status code. Those failures
    /// become a <see cref="ProblemDetails"/>, so callers stay on the <c>ApiResult</c> path and can show a
    /// message instead of crashing the page.
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
