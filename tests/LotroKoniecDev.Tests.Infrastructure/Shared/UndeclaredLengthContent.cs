using System.Net;

namespace LotroKoniecDev.Tests.Infrastructure.Shared;

/// <summary>
/// Streams the requested number of bytes without ever declaring a length
/// (<see cref="TryComputeLength"/> returns <c>false</c>), forcing the reader onto its
/// streaming code path — a chunked-style response from a server that may lie about size.
/// </summary>
internal sealed class UndeclaredLengthContent : HttpContent
{
    private readonly long _sizeInBytes;

    public UndeclaredLengthContent(long sizeInBytes)
    {
        _sizeInBytes = sizeInBytes;
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        byte[] chunk = new byte[81920];
        long remaining = _sizeInBytes;
        while (remaining > 0)
        {
            int take = (int)Math.Min(chunk.Length, remaining);
            await stream.WriteAsync(chunk.AsMemory(0, take));
            remaining -= take;
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}
