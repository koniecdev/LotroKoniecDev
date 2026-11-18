using System.Net;

namespace LotroKoniecDev.Tests.Infrastructure.Shared;

/// <summary>
/// Declares no length and sends its bytes only after a delay, as a slow line does. Both ways of
/// reading the body wait on the reader's token, so a token that ends early cancels the read.
/// </summary>
internal sealed class SlowContent : HttpContent
{
    private readonly byte[] _body;
    private readonly TimeSpan _delay;

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        await Task.Delay(_delay, cancellationToken);
        await stream.WriteAsync(_body, cancellationToken);
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
        Task.FromResult<Stream>(new SlowStream(_body, _delay));

    protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(CancellationToken.None);

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }

    public SlowContent(byte[] body, TimeSpan delay)
    {
        _body = body;
        _delay = delay;
    }

    private sealed class SlowStream : Stream
    {
        private readonly byte[] _body;
        private readonly TimeSpan _delay;
        private int _position;
        private bool _waited;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_waited)
            {
                await Task.Delay(_delay, cancellationToken);
                _waited = true;
            }

            int read = Math.Min(buffer.Length, _body.Length - _position);
            _body.AsMemory(_position, read).CopyTo(buffer);
            _position += read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("The body is read asynchronously, so the wait can see the token.");

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public SlowStream(byte[] body, TimeSpan delay)
        {
            _body = body;
            _delay = delay;
        }
    }
}
