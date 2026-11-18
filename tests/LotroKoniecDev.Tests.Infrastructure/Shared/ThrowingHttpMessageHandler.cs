namespace LotroKoniecDev.Tests.Infrastructure.Shared;

/// <summary>An in-memory handler that fails every request the way a machine with no network does.</summary>
internal sealed class ThrowingHttpMessageHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new HttpRequestException("No such host is known.");
}
