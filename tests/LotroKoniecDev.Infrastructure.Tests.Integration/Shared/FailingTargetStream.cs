namespace LotroKoniecDev.Infrastructure.Tests.Integration.Shared;

/// <summary>
/// A download target on a disk that fills up: a write, the flush or the close fails as a full disk
/// does. A file stream that failed a write keeps the bytes in its buffer and fails again when it is
/// closed, which a failing write together with a failing close plays.
/// </summary>
internal sealed class FailingTargetStream : Stream
{
    private readonly bool _failWrite;
    private readonly bool _failFlush;
    private readonly bool _failClose;

    public bool IsClosed { get; private set; }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get { throw new NotSupportedException(); }
        set { throw new NotSupportedException(); }
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        _failWrite ? ValueTask.FromException(DiskFull()) : ValueTask.CompletedTask;

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (_failWrite)
        {
            throw DiskFull();
        }
    }

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        _failFlush ? Task.FromException(DiskFull()) : Task.CompletedTask;

    public override void Flush()
    {
        if (_failFlush)
        {
            throw DiskFull();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        IsClosed = true;
        if (_failClose)
        {
            throw DiskFull();
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public FailingTargetStream(bool failWrite, bool failFlush, bool failClose)
    {
        _failWrite = failWrite;
        _failFlush = failFlush;
        _failClose = failClose;
    }

    private static IOException DiskFull() => new("There is not enough space on the disk.");
}
