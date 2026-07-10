using System.Net;

namespace LotroKoniecDev.Tests.Infrastructure.Shared;

/// <summary>
/// Declares no length and stalls the body write until the caller's cancellation token fires —
/// a hostile server that accepts the request and then feeds no bytes. The stall is bounded by a
/// fallback delay, so a mis-wired cancellation token fails the test instead of hanging the run.
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
