namespace CodexOpenCodexNative.Adapters;

public sealed class TeeStream : Stream
{
    private readonly Stream _inner;
    private readonly Stream _tee;

    public TeeStream(Stream inner, Stream tee)
    {
        _inner = inner;
        _tee = tee;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        if (read > 0)
            _tee.Write(buffer, offset, read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken);
        if (read > 0)
            await _tee.WriteAsync(buffer[..read], cancellationToken);
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = await _inner.ReadAsync(buffer, offset, count, cancellationToken);
        if (read > 0)
            await _tee.WriteAsync(buffer, offset, read, cancellationToken);
        return read;
    }

    public override void Flush()
    {
        _tee.Flush();
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tee.Flush();
            _tee.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _tee.FlushAsync();
        await _tee.DisposeAsync();
        await base.DisposeAsync();
    }
}
