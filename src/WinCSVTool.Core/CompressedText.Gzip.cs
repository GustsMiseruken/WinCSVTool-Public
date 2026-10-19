using ICSharpCode.SharpZipLib.Checksum;
using ICSharpCode.SharpZipLib.Zip.Compression;

namespace WinCSVTool.Core;

public sealed partial class CompressedText
{
    private sealed class StrictGzipStream(Stream input, CancellationToken token) : Stream
    {
        private readonly byte[] compressed = new byte[65536];
        private readonly Inflater inflater = new(true);
        private readonly Crc32 crc = new();
        private int cursor, end;
        private bool active, ended;
        private uint memberLength;

        private int Byte()
        {
            token.ThrowIfCancellationRequested();
            if(cursor==end) { end=input.Read(compressed,0,compressed.Length); cursor=0; if(end==0)return -1; }
            return compressed[cursor++];
        }
        private int RequiredByte() {var value=Byte();return value<0?throw new InvalidDataException("gzip memberが途中で切断されています。"):value;}
        private void Header(int first)
        {
            var headerCrc=new Crc32();
            void Add(int b)=>headerCrc.Update(b);
            int Next(){var b=RequiredByte();Add(b);return b;}
            Add(first); if(first!=0x1f || Next()!=0x8b || Next()!=8)throw new InvalidDataException("gzip memberのヘッダーが不正です。");
            var flags=Next();if((flags&0xe0)!=0)throw new InvalidDataException("gzip memberの予約フラグが不正です。");
            for(var i=0;i<6;i++)Next();
            if((flags&4)!=0){var size=Next()|(Next()<<8);for(var i=0;i<size;i++)Next();}
            foreach(var flag in new[]{8,16})if((flags&flag)!=0){var size=0;while(Next()!=0)if(++size>65536)throw new InvalidDataException("gzip memberのヘッダー文字列が長すぎます。");}
            if((flags&2)!=0){var expected=RequiredByte()|(RequiredByte()<<8);if(expected!=(headerCrc.Value&0xffff))throw new InvalidDataException("gzipヘッダーCRCが一致しません。");}
            inflater.Reset();crc.Reset();memberLength=0;active=true;
        }
        private uint UInt32()=> (uint)RequiredByte()|((uint)RequiredByte()<<8)|((uint)RequiredByte()<<16)|((uint)RequiredByte()<<24);
        public override int Read(byte[] buffer,int offset,int count)
        {
            ArgumentNullException.ThrowIfNull(buffer); if(offset<0||count<0||offset>buffer.Length-count)throw new ArgumentOutOfRangeException(nameof(count));
            if(count==0)return 0;
            while(!ended)
            {
                token.ThrowIfCancellationRequested();
                if(!active){var first=Byte();if(first<0){ended=true;return 0;}Header(first);}
                if(inflater.IsNeedingInput)
                {
                    if(cursor==end){end=input.Read(compressed,0,compressed.Length);cursor=0;if(end==0)throw new InvalidDataException("gzipの圧縮データが途中で切断されています。");}
                    inflater.SetInput(compressed,cursor,end-cursor);cursor=end;
                }
                int produced;
                try{produced=inflater.Inflate(buffer,offset,count);}
                catch(ICSharpCode.SharpZipLib.SharpZipBaseException error){throw new InvalidDataException("gzipの圧縮データが不正です。",error);}
                if(produced>0){crc.Update(new ArraySegment<byte>(buffer,offset,produced));memberLength=unchecked(memberLength+(uint)produced);}
                if(inflater.IsFinished)
                {
                    cursor=end-inflater.RemainingInput;
                    if(UInt32()!=(uint)crc.Value || UInt32()!=memberLength)throw new InvalidDataException("gzip memberのCRCまたは展開サイズが一致しません。");
                    active=false;
                }
                else if(produced==0&&!inflater.IsNeedingInput)throw new InvalidDataException("gzipの展開処理を続行できません。");
                if(produced>0)return produced;
            }
            return 0;
        }
        protected override void Dispose(bool disposing){if(disposing)input.Dispose();base.Dispose(disposing);}
        public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;
        public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override void Flush(){}public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException();public override void SetLength(long l)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int c)=>throw new NotSupportedException();
    }
}
