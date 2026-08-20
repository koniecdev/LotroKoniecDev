using System.Net;

namespace LotroKoniecDev.Tests.Infrastructure.Shared;

/// <summary>
/// Sends the requested number of bytes without ever declaring a length, because
/// <see cref="TryComputeLength"/> returns <c>false</c>. That forces the reader down its streaming path,
/// like a chunked response from a server that may lie about the size.
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
