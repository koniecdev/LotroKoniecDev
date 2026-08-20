using System.Net;

namespace LotroKoniecDev.Tests.Infrastructure.Shared;

/// <summary>
/// Declares no length and holds the body back until the caller's cancellation token fires. It plays a
/// hostile server that accepts the request and then sends nothing. The wait has a maximum length, so a
/// cancellation token that was wired up wrongly fails the test instead of hanging the run.
/// </summary>
internal sealed class StallingContent : HttpContent
{
    private static readonly TimeSpan StallFallback = TimeSpan.FromSeconds(10);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        await Task.Delay(StallFallback, cancellationToken);
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}
