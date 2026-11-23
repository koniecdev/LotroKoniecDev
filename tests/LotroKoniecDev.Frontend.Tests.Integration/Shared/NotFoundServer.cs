using System.Net;

namespace LotroKoniecDev.Frontend.Tests.Integration.Shared;

/// <summary>
/// A server that answers 404 to every call, so a page that loads data shows its calm notice at once and
/// never calls out of the test.
/// </summary>
public sealed class NotFoundServer : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
}
