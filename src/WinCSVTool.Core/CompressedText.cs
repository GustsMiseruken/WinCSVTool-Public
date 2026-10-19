using System.IO.Compression;
using System.Text;

namespace WinCSVTool.Core;

/// <summary>Bounded text input with gzip framing validation and strict encoding.</summary>
public sealed partial class CompressedText : IDisposable
{
    private readonly FileStream file;
    public StreamReader Reader { get; }
    public string EncodingName { get; }
    public long BytesRead => file.Position;
    public long TotalBytes => file.Length;
    public bool IsCompressed { get; }

    public static CompressedText OpenRead(string path, string encodingName = "auto", CancellationToken token = default) => new(path, encodingName, token);

    private CompressedText(string path, string encodingName, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        Stream? input = null;
        try
        {
            var magic = new byte[2]; var count = file.ReadAtLeast(magic, 2, false); file.Position = 0;
            IsCompressed = count == 2 && magic[0] == 0x1f && magic[1] == 0x8b;
            if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) && !IsCompressed) throw new InvalidDataException("gzipヘッダーがないか、ファイルが切断されています。");
            input = IsCompressed ? new StrictGzipStream(file, token) : file;
            var prefix = new byte[65536]; var used = 0;
            while (used < prefix.Length)
            {
                token.ThrowIfCancellationRequested(); var read = input.Read(prefix, used, prefix.Length - used); if (read == 0) break; used += read;
            }
            EncodingName = encodingName.Equals("auto", StringComparison.OrdinalIgnoreCase) ? EncodingDetector.Detect(prefix.AsSpan(0, used), used < prefix.Length) : encodingName;
            var encoding = EncodingDetector.Resolve(EncodingName);
            byte[] preamble = encoding.CodePage switch { 65001 => [0xef,0xbb,0xbf], 1200 => [0xff,0xfe], 1201 => [0xfe,0xff], _ => [] };
            var skip = preamble.Length > 0 && prefix.AsSpan(0,used).StartsWith(preamble) ? preamble.Length : 0;
            Reader = new StreamReader(new ReplayStream(input, prefix, skip, used, token), encoding, false, 65536);
            EncodingName = encoding.WebName;
        }
        catch { input?.Dispose(); file.Dispose(); throw; }
    }

    public static Stream OpenWrite(Stream destination, string path) => path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
        ? new GZipStream(destination, CompressionLevel.Fastest, true) : new NonClosingStream(destination);
    public void Dispose() { Reader.Dispose(); file.Dispose(); }

    private sealed class ReplayStream(Stream input, byte[] prefix, int start, int end, CancellationToken token) : Stream
    {
        private int cursor = start;
        public override int Read(byte[] buffer,int offset,int count)
        {
            token.ThrowIfCancellationRequested(); if (cursor >= end) return input.Read(buffer,offset,count);
            var take = Math.Min(count,end-cursor); prefix.AsSpan(cursor,take).CopyTo(buffer.AsSpan(offset,take)); cursor+=take; return take;
        }
        protected override void Dispose(bool disposing) { if(disposing) input.Dispose(); base.Dispose(disposing); }
        public override bool CanRead=>true; public override bool CanSeek=>false; public override bool CanWrite=>false;
        public override long Length=>throw new NotSupportedException(); public override long Position {get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override void Flush(){} public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException(); public override void SetLength(long l)=>throw new NotSupportedException(); public override void Write(byte[] b,int o,int c)=>throw new NotSupportedException();
    }
    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override void Write(byte[] b,int o,int c)=>inner.Write(b,o,c); public override void Write(ReadOnlySpan<byte> b)=>inner.Write(b); public override void Flush()=>inner.Flush();
        public override bool CanRead=>false;public override bool CanSeek=>false;public override bool CanWrite=>true;
        public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override int Read(byte[] b,int o,int c)=>throw new NotSupportedException(); public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException();public override void SetLength(long l)=>throw new NotSupportedException();
    }
}
