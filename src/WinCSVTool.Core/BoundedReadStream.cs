namespace WinCSVTool.Core;

/// <summary>Caps decompressed input before consumers can retain unbounded metadata.</summary>
internal sealed class BoundedReadStream(Stream inner, long maximumBytes, CancellationToken token) : Stream
{
    private long consumed;
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => consumed; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        token.ThrowIfCancellationRequested();
        if (buffer.IsEmpty) return 0;
        var available = Math.Min(buffer.Length, maximumBytes - consumed + 1);
        var read = inner.Read(buffer[..(int)available]);
        consumed += read;
        if (consumed > maximumBytes) throw new InvalidDataException("XLSX の展開されたメタデータが上限を超えています。");
        token.ThrowIfCancellationRequested();
        return read;
    }
    public override int ReadByte()
    {
        Span<byte> buffer = stackalloc byte[1];
        return Read(buffer) == 0 ? -1 : buffer[0];
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
