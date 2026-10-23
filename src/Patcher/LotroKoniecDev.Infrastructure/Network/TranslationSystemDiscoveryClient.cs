using System.Net.Http.Headers;
using System.Text.Json;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.Infrastructure.Network;

/// <summary>
/// Reads the TMS service document from the configured base URL, the anonymous discovery root, so the
/// CLI finds its download endpoint by link relation instead of a fixed route (ADR-0041, #611).
/// <para>
/// Links are opt-in: the server adds them only to a request that accepts
/// <see cref="MediaTypes.HateoasJson"/>. The constant comes from the shared Hateoas abstractions
/// because a drifted Accept header would quietly return a document with no links, and every install
/// would stay on its cached endpoint forever.
/// </para>
/// </summary>
public sealed class TranslationSystemDiscoveryClient : ITranslationSystemDiscoveryClient
{
    /// <summary>
    /// The largest service document we read (#394). It is a few KB of links, so 1 MiB leaves room to
    /// spare while a hostile or broken server cannot use up our memory.
    /// </summary>
    public const long MaxResponseContentBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;

    public TranslationSystemDiscoveryClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<Result<IReadOnlyList<DiscoveredLink>>> FetchLinksAsync(
        string baseUrl, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        if (!Uri.TryCreate($"{baseUrl.TrimEnd('/')}/", UriKind.Absolute, out Uri? requestUri))
        {
            return Result.Failure<IReadOnlyList<DiscoveredLink>>(
                DomainErrors.TranslationFileSync.NetworkError($"'{baseUrl}' is not an absolute URI."));
        }

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, requestUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypes.HateoasJson));

            // ResponseHeadersRead lets the size limit run before HttpClient buffers the body. It also
            // takes the body read out of HttpClient.Timeout, so a linked token applies that timeout
            // to the whole fetch.
            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_httpClient.Timeout);

            using HttpResponseMessage response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);

            response.EnsureSuccessStatusCode();

            Maybe<string> body = await BoundedResponseReader.TryReadAsStringAsync(
                response.Content, MaxResponseContentBytes, timeoutCts.Token);
            if (body.HasNoValue)
            {
                return Result.Failure<IReadOnlyList<DiscoveredLink>>(
                    DomainErrors.TranslationFileSync.ResponseTooLarge(MaxResponseContentBytes));
            }

            return ReadLinks(body.Value);
        }
        catch (HttpRequestException ex)
        {
            return Result.Failure<IReadOnlyList<DiscoveredLink>>(
                DomainErrors.TranslationFileSync.NetworkError(ex.Message));
        }
        catch (OperationCanceledException)
        {
            return Result.Failure<IReadOnlyList<DiscoveredLink>>(
                DomainErrors.TranslationFileSync.NetworkError("The request timed out."));
        }
    }

    private static Result<IReadOnlyList<DiscoveredLink>> ReadLinks(string body)
    {
        ServiceDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<ServiceDocument>(body, SerializerOptions);
        }
        catch (JsonException ex)
        {
            // Anything that is not the service document, such as a proxy error page or an HTML login
            // page, means discovery failed. It must not crash the CLI.
            return Result.Failure<IReadOnlyList<DiscoveredLink>>(
                DomainErrors.TranslationFileSync.NetworkError($"The service document could not be read: {ex.Message}"));
        }

        if (document?.Links is null)
        {
            return Result.Failure<IReadOnlyList<DiscoveredLink>>(
                DomainErrors.TranslationFileSync.NetworkError("The service document carried no links."));
        }

        List<DiscoveredLink> links = [];
        foreach (ServiceDocumentLink link in document.Links)
        {
            if (link is { Href: not null, Rel: not null, Method: not null })
            {
                links.Add(new DiscoveredLink(link.Href, link.Rel, link.Method));
            }
        }

        return Result.Success<IReadOnlyList<DiscoveredLink>>(links);
    }

    private sealed record ServiceDocument(IReadOnlyList<ServiceDocumentLink>? Links);

    private sealed record ServiceDocumentLink(string? Href, string? Rel, string? Method);
}
