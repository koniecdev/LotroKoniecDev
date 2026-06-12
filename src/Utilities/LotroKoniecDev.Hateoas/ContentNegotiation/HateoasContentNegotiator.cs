using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.Hateoas.ContentNegotiation;

/// <summary>
/// RFC 7231 / RFC 9110 compliant Accept-header parser that selects between
/// the plain JSON representation and the HATEOAS vendor representation based
/// on client-declared media-type quality factors.
///
/// <para>Decision rules:</para>
/// <list type="bullet">
///   <item>No Accept header or empty header -> plain JSON (no HATEOAS).</item>
///   <item>Wildcards (<c>*/*</c>, <c>application/*</c>) -> plain JSON (HATEOAS is strictly opt-in).</item>
///   <item>HATEOAS vendor type explicitly listed with quality &gt;= JSON quality -> HATEOAS.</item>
///   <item>Otherwise -> plain JSON.</item>
/// </list>
///
/// <para>
/// Ties between the two specific media types favour the vendor type because
/// the client has explicitly requested it — silently dropping requested
/// hypermedia would violate the principle of least astonishment.
/// </para>
///
/// <para>
/// This is a pure function over the request's Accept header, so it is exposed
/// as a static class: there is no state, no dependencies, and no meaningful
/// way to substitute it (integration tests exercise the real implementation).
/// Keeping it static avoids a one-implementation interface resolved from DI
/// just to be located inside <see cref="HateoasNegotiatedResult{T}"/>.
/// </para>
///
/// <para>
/// Note: when a client sends an Accept header that matches neither media
/// type (e.g. <c>text/html</c>), RFC 9110 §15.5.7 allows returning
/// <c>406 Not Acceptable</c>. We intentionally fall back to plain JSON
/// instead — the pragmatic REST convention — because the API has no other
/// representations to offer.
/// </para>
/// </summary>
internal static class HateoasContentNegotiator
{
    public static bool ShouldIncludeHateoas(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Headers.TryGetValue(HeaderNames.Accept, out StringValues acceptValues)
            || acceptValues.Count == 0)
        {
            return false;
        }

        if (!MediaTypeHeaderValue.TryParseList(acceptValues, out IList<MediaTypeHeaderValue>? parsed))
        {
            return false;
        }

        double hateoasQuality = -1;
        double jsonQuality = -1;

        foreach (MediaTypeHeaderValue mediaType in parsed)
        {
            if (!mediaType.MediaType.HasValue)
            {
                continue;
            }

            double quality = mediaType.Quality ?? 1.0;

            if (mediaType.MediaType.Equals(MediaTypes.HateoasJson, StringComparison.OrdinalIgnoreCase))
            {
                hateoasQuality = Math.Max(hateoasQuality, quality);
            }
            else if (mediaType.MediaType.Equals(MediaTypes.Json, StringComparison.OrdinalIgnoreCase))
            {
                jsonQuality = Math.Max(jsonQuality, quality);
            }
        }

        // HATEOAS vendor type was not listed (or was rejected via q=0).
        if (hateoasQuality <= 0)
        {
            return false;
        }

        // HATEOAS wins on ties (more specific, explicitly requested).
        return hateoasQuality >= jsonQuality;
    }
}
