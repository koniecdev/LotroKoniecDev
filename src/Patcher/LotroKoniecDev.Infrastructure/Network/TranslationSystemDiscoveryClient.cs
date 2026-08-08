using System.Net.Http.Headers;
using System.Text.Json;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.Infrastructure.Network;

/// <summary>
/// Reads the TMS service document from the configured base URL — the anonymous discovery root
/// (#608) — so the CLI can resolve its download endpoint by link relation instead of shipping a
/// route it could never change (ADR-0041 / #611).
/// <para>
/// Links are opt-in through content negotiation: the server attaches them only to a request that
/// accepts <see cref="MediaTypes.HateoasJson"/>, which is why that constant is taken from the shared
/// Hateoas abstractions rather than re-typed here — a drifted Accept header would silently yield a
/// link-less document and pin every install on its cached endpoint forever.
/// </para>
/// </summary>
public sealed class TranslationSystemDiscoveryClient : ITranslationSystemDiscoveryClient
{
    /// <summary>
    /// Hard cap on the service document (AUDIT-SEC-04 / #394). It is a short list of links — a few KB —
    /// so 1 MiB is generous headroom while a hostile or misbehaving server cannot exhaust process memory.
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

            // ResponseHeadersRead keeps HttpClient from buffering the whole body before the size cap
            // can run, which also moves the body read out of HttpClient.Timeout's scope — so the
            // timeout is re-applied around the entire fetch via a linked token.
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
            // Anything that is not the service document — a proxy error page, an HTML login wall —
            // is a failed discovery, not a crash.
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
