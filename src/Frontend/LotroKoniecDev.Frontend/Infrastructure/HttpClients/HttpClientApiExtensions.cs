using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LotroKoniecDev.Frontend.Infrastructure.Errors;
using Microsoft.AspNetCore.Mvc;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace LotroKoniecDev.Frontend.Infrastructure.HttpClients;

/// <summary>
/// Turns <see cref="HttpClient"/> calls into <see cref="ApiResult"/>. A transport failure with no status
/// (a Polly timeout, an open circuit, a socket error) becomes a Polish <c>ProblemDetails</c>. Nothing here
/// throws because of what came off the wire: a body that is not the API's answer is a failed result,
/// never an exception escaping the render.
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

        /// <summary>
        /// For a body too large to hold in memory, such as the translation file (#715). On success the
        /// caller owns the stream and disposes it, which also ends the response.
        /// </summary>
        public async Task<ApiResult<ApiBodyStream>> GetBodyStreamAsync(
            string uri,
            CancellationToken cancellationToken = default)
        {
            using HttpRequestMessage request = new(
                HttpMethod.Get,
                new Uri(uri, UriKind.RelativeOrAbsolute));
            HttpResponseMessage? response = null;
            try
            {
                response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);
                    ApiBodyStream download = new(body, response.Content.Headers.ContentLength);
                    response = null;
                    return ApiResult.Success(download);
                }

                string content = await ReadBodyAsUtf8Async(response.Content, cancellationToken);
                return ApiResult.Failure<ApiBodyStream>(ParseProblemDetails(content, response));
            }
            catch (Exception ex) when (IsTransportFailure(ex))
            {
                return ApiResult.Failure<ApiBodyStream>(MapTransportFailureToProblemDetails(ex));
            }
            finally
            {
                response?.Dispose();
            }
        }

        public async Task<ApiResult> PostApiResultAsync(
            string uri,
            object body,
            CancellationToken cancellationToken = default)
        {
            using HttpRequestMessage request = CreateJsonRequest(HttpMethod.Post, uri, body);
            return await SendForApiResultAsync(httpClient, request, cancellationToken);
        }

        public async Task<ApiResult<T>> PostApiResultAsync<T>(
            string uri,
            object body,
            CancellationToken cancellationToken = default)
        {
            using HttpRequestMessage request = CreateJsonRequest(HttpMethod.Post, uri, body);
            return await SendForApiResultAsync<T>(httpClient, request, cancellationToken);
        }

        /// <summary>
        /// For an endpoint that answers in headers instead of a body, such as the <c>204</c> with
        /// <c>X-Deletion-Finalizes-At</c> when an account deletion is scheduled.
        /// </summary>
        public async Task<ApiResult<ApiResponseHeaders>> PostForHeadersApiResultAsync(
            string uri,
            object body,
            CancellationToken cancellationToken = default)
        {
            using HttpRequestMessage request = CreateJsonRequest(HttpMethod.Post, uri, body);
            try
            {
                using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    return ApiResult.Success(ApiResponseHeaders.From(response.Headers));
                }

                string content = await ReadBodyAsUtf8Async(response.Content, cancellationToken);
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
            using HttpRequestMessage request = CreateJsonRequest(HttpMethod.Put, uri, body);
            return await SendForApiResultAsync(httpClient, request, cancellationToken);
        }

        public async Task<ApiResult<T>> PutApiResultAsync<T>(
            string uri,
            object body,
            CancellationToken cancellationToken = default)
        {
            using HttpRequestMessage request = CreateJsonRequest(HttpMethod.Put, uri, body);
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
            using HttpRequestMessage request = CreateJsonRequest(HttpMethod.Patch, uri, body);
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

    /// <summary>
    /// The body's runtime type goes into <c>JsonContent</c> so the resilience pipeline can see a password
    /// confirmation and send it only once (ADR-0053). <c>JsonContent.Create&lt;object&gt;</c> would lose that.
    /// </summary>
    private static HttpRequestMessage CreateJsonRequest(HttpMethod method, string uri, object body) =>
        new(method, new Uri(uri, UriKind.RelativeOrAbsolute))
        {
            Content = JsonContent.Create(body, body.GetType(), options: JsonOptions)
        };

    private static async Task<ApiResult<T>> SendForApiResultAsync<T>(
        HttpClient httpClient,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
            string content = await ReadBodyAsUtf8Async(response.Content, cancellationToken);

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

            string content = await ReadBodyAsUtf8Async(response.Content, cancellationToken);
            return ApiResult.Failure(ParseProblemDetails(content, response));
        }
        catch (Exception ex) when (IsTransportFailure(ex))
        {
            return ApiResult.Failure(MapTransportFailureToProblemDetails(ex));
        }
    }

    /// <summary>
    /// Never decodes by the charset in <c>Content-Type</c>: that throws for a name .NET does not know
    /// ("utf8") or refuses ("utf-7") (#972). JSON between services is UTF-8 (RFC 8259 §8.1), and so is the
    /// translation file. A UTF-8 byte order mark is dropped.
    /// </summary>
    private static async Task<string> ReadBodyAsUtf8Async(HttpContent content, CancellationToken cancellationToken)
    {
        byte[] body = await content.ReadAsByteArrayAsync(cancellationToken);
        int bomLength = body.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? Encoding.UTF8.Preamble.Length : 0;
        return Encoding.UTF8.GetString(body, bomLength, body.Length - bomLength);
    }

    /// <summary>
    /// A success status does not prove the body is the API's answer: a reverse proxy serves its
    /// maintenance page with a <c>200</c>. So an unreadable, <c>null</c> or empty body is a failure with
    /// only a status (#637, #638, #653), never a <c>null</c> value for the caller. Every generic verb
    /// promises a value, so a <c>204</c> fails here too. It reports <c>502</c>, because a download route
    /// would otherwise answer the browser with a problem carrying a <c>200</c>.
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
    /// Not every error has a problem body: a bearer challenge's <c>401</c> is empty, and a service that is
    /// down answers with the proxy's HTML. Those become a bare problem with the real status, so checks such
    /// as <c>IsUnauthorized</c> keep working.
    /// </summary>
    private static ProblemDetails ParseProblemDetails(string content, HttpResponseMessage response)
    {
        // The typed clients follow no redirects (#830), so a 3xx is unusable. Its status must not reach
        // the browser either: a download route would pass on a redirect with no Location.
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            return ApiProblemCopy.StatusOnly(StatusCodes.Status502BadGateway);
        }

        if (!string.IsNullOrWhiteSpace(content))
        {
            try
            {
                ProblemDetails? problem = JsonSerializer.Deserialize<ProblemDetails>(content, JsonOptions);
                if (problem is not null)
                {
                    problem.Status ??= (int)response.StatusCode;

                    // Off the wire means English, so it may not carry the Frontend-authored marker
                    // (ADR-0044 §2).
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
