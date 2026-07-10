namespace LotroKoniecDev.Tests.Infrastructure.Shared;

/// <summary>
/// In-memory <see cref="HttpMessageHandler"/> returning one prepared response — no real network.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpResponseMessage _response;

    public StubHttpMessageHandler(HttpResponseMessage response)
    {
        _response = response;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(_response);
}
