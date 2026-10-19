using System.Net;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.Contracts.Discovery;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;

namespace LotroKoniecDev.Frontend.Tests.Integration.Shared;

/// <summary>
/// The translation API as the dashboard sees it, through the website's typed client. The service document
/// is open to everyone, so a refused token gets the anonymous links there, never a 401. The statistics need a
/// sign-in, so a refused token gets a 401 there.
/// </summary>
public sealed class DashboardTranslationSystem : HttpMessageHandler
{
    /// <summary>The name ASP.NET Core gives the typed client, which is internal to the Frontend.</summary>
    public const string ClientName = "ITranslationSystemClient";

    private const string TmsOrigin = "https://tms.staging.invalid";
    private const string StatsHref = $"{TmsOrigin}/advertised/translation-stats";

    private int _unauthorized;

    public bool AcceptsTheToken { get; set; } = true;

    /// <summary>How many 401 answers the API sent, so a test can tell which path marked the session.</summary>
    public int Unauthorized => _unauthorized;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Uri uri = request.RequestUri ?? throw new InvalidOperationException("A call with no address.");
        HttpResponseMessage response = uri.GetLeftPart(UriPartial.Path) switch
        {
            $"{TmsOrigin}/" => StubAnswers.HateoasJson(new DiscoveryResponse("tms")
            {
                Links = AcceptsTheToken
                    ?
                    [
                        new LinkDto(StatsHref, Rels.TranslationStats, "GET"),
                        new LinkDto($"{TmsOrigin}/advertised/contributions", Rels.ContributionDataExport, "GET")
                    ]
                    : []
            }),
            StatsHref when AcceptsTheToken => StubAnswers.HateoasJson(new TranslationStatsResponse(4, 3, 2, 1)),
            StatsHref => Refuse(),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        };

        return Task.FromResult(response);
    }

    private HttpResponseMessage Refuse()
    {
        Interlocked.Increment(ref _unauthorized);
        return new HttpResponseMessage(HttpStatusCode.Unauthorized);
    }
}
