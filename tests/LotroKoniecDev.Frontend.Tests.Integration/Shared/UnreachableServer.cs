namespace LotroKoniecDev.Frontend.Tests.Integration.Shared;

/// <summary>A server that is down: every call fails before an answer arrives.</summary>
public sealed class UnreachableServer : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        Task.FromException<HttpResponseMessage>(new HttpRequestException("Connection refused."));
}
