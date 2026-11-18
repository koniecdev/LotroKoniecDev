using System.Net;

namespace LotroKoniecDev.Tests.Infrastructure.Shared;

/// <summary>
/// A body that streams some bytes and then breaks off, as a dropped connection does in the middle of
/// a download. It declares no length, so the reader takes its streaming path.
/// </summary>
internal sealed class BreakingContent : HttpContent
{
    private readonly int _bytesBeforeBreak;

    protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
        Task.FromResult<Stream>(new BreakingStream(_bytesBeforeBreak));

    protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(CancellationToken.None);

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        throw new IOException("The connection was reset.");

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }

    public BreakingContent(int bytesBeforeBreak)
    {
        _bytesBeforeBreak = bytesBeforeBreak;
    }

    private sealed class BreakingStream : Stream
    {
        private int _remaining;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining == 0)
            {
                throw new IOException("The connection was reset.");
            }

            int read = Math.Min(count, _remaining);
            Array.Clear(buffer, offset, read);
            _remaining -= read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public BreakingStream(int bytesBeforeBreak)
        {
            _remaining = bytesBeforeBreak;
        }
    }
}
