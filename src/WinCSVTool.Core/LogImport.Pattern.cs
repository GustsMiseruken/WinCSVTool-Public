using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;

namespace WinCSVTool.Core;

internal sealed class PatternLogParser : ILogRecordParser
{
    private readonly Regex regex;
    private readonly LogEscapeMode escape;
    private readonly Encoding byteEncoding;
    public IReadOnlyList<string> Headers { get; }
    internal PatternLogParser(string id,string pattern,LogEscapeMode escape,string encodingName)
    {
        this.escape=escape;
        byteEncoding=EncodingDetector.Resolve(encodingName.Equals("auto",StringComparison.OrdinalIgnoreCase)?"utf-8":encodingName);
        if(pattern is "common" or "combined")pattern=id=="nginx"?"$remote_addr - $remote_user [$time_local] \"$request\" $status $body_bytes_sent"+(pattern=="combined"?" \"$http_referer\" \"$http_user_agent\"":""):"%h %l %u %t \"%r\" %s %b"+(pattern=="common"?"":id=="jboss"?" \"%{i,Referer}\" \"%{i,User-Agent}\"":" \"%{Referer}i\" \"%{User-Agent}i\"");
        if(pattern.Length is 0 or >32768)throw new ArgumentException("ログ書式本体を1～32,768文字で指定してください。");
        var tokens=new Regex(id=="nginx"?@"\$\{[A-Za-z0-9_]+\}|\$[A-Za-z0-9_]+":@"%[!0-9,]*[<>]?(?:\{[^{}]+\})?[A-Za-z%]|%\{[^{}]+\}",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1)).Matches(pattern);
        var headers=new List<string>();var expression=new StringBuilder("\\A");var at=0;
        foreach(Match token in tokens)
        {
            var literal=pattern[at..token.Index];
            if(headers.Count>0 && literal.Length==0)throw new ArgumentException("ログ変数の間に区切り文字が必要です。");
            expression.Append(Literal(literal));
            if(token.Value=="%%"){expression.Append('%');at=token.Index+token.Length;continue;}
            var next=token.Index+token.Length;var before=token.Index>0?pattern[token.Index-1]:'\0';var after=next<pattern.Length?pattern[next]:'\0';
            var name=id=="nginx"?token.Value.TrimStart('$').Trim('{','}'):token.Value;
            var time=id!="nginx"&&token.Value=="%t";
            var field=before=='"'&&after=='"'?@"(?:\\.|[^""\\])*":before=='['&&after==']'?@"(?:\\.|[^\\\]])*":@"(?:\\.|[^\s\\])+";
            if(time){expression.Append(@"\[");field=@"(?:\\.|[^\\\]])*";}
            expression.Append("(?<f").Append(headers.Count).Append('>').Append(field).Append(')');
            if(time)expression.Append(@"\]");
            headers.Add(name);at=next;
        }
        if(headers.Count is <1 or >128)throw new ArgumentException("ログ書式には1～128個の変数を指定してください。");
        expression.Append(Literal(pattern[at..])).Append("\\z");
        regex=new Regex(expression.ToString(),RegexOptions.CultureInvariant|RegexOptions.NonBacktracking,TimeSpan.FromSeconds(2));Headers=headers;
    }
    private static string Literal(string value)
    {
        value=value.Replace("\\t","\t",StringComparison.Ordinal).Replace("\\\"","\"",StringComparison.Ordinal);
        var result=new StringBuilder();for(var i=0;i<value.Length;i++)
        {
            if(value[i] is ' ' or '\t'){while(i+1<value.Length&&value[i+1] is ' ' or '\t')i++;result.Append("[ \\t]+");}
            else result.Append(Regex.Escape(value[i].ToString()));
        }
        return result.ToString();
    }
    public string[] Parse(string line,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();var match=regex.Match(line);
        if(!match.Success)throw new FormatException("設定したログ書式と一致しません。引用符・角括弧・変数の順序を確認してください。");
        var values=new string[Headers.Count];for(var i=0;i<values.Length;i++){token.ThrowIfCancellationRequested();values[i]=Decode(match.Groups["f"+i].Value,escape,byteEncoding);}
        return values;
    }
    private static string Decode(string value,LogEscapeMode escape,Encoding byteEncoding)
    {
        if(escape==LogEscapeMode.None||!value.Contains('\\'))return value;
        if(escape==LogEscapeMode.Default)return DecodeDefault(value,byteEncoding);
        var result=new StringBuilder(value.Length);var utf8=EncodingDetector.Resolve("utf-8");
        for(var i=0;i<value.Length;i++)
        {
            if(value[i]!='\\'){result.Append(value[i]);continue;}
            if(i+1>=value.Length)throw new FormatException("ログ値末尾のescapeが不正です。");
            var next=value[++i];
            if(escape==LogEscapeMode.Json && next=='u')
            {
                if(i+4>=value.Length||!ushort.TryParse(value.AsSpan(i+1,4),System.Globalization.NumberStyles.HexNumber,System.Globalization.CultureInfo.InvariantCulture,out var code))throw new FormatException("ログ値のUnicode escapeが不正です。");
                result.Append((char)code);i+=4;continue;
            }
            result.Append(DecodeEscape(next));
        }
        var decoded=result.ToString();_ = utf8.GetByteCount(decoded);return decoded;
    }
    private static char DecodeEscape(char next)=>next switch{'n'=>'\n','r'=>'\r','t'=>'\t','b'=>'\b','f'=>'\f','v'=>'\v','"'=>'"','\\'=>'\\','/'=>'/',' '=>' ',_=>throw new FormatException("ログ値のescapeが不正です。escape方式を確認してください。")};

    private static string DecodeDefault(string value,Encoding encoding)
    {
        var result=new StringBuilder(value.Length);
        for(var i=0;i<value.Length;)
        {
            // Literal Unicode has already been decoded by the input reader. Keep it
            // intact, and require any preceding encoded byte run to be complete.
            if(value[i]>127){result.Append(value[i++]);continue;}
            var start=i;while(i<value.Length&&value[i]<=127)i++;
            if(ContainsHexEscape(value,start,i))DecodeDefaultBytes(value,start,i,encoding,result);
            else
            {
                for(var at=start;at<i;at++)
                {
                    if(value[at]!='\\'){result.Append(value[at]);continue;}
                    if(++at>=i)throw new FormatException("ログ値末尾のescapeが不正です。");
                    result.Append(DecodeEscape(value[at]));
                }
            }
        }
        var decoded=result.ToString();_ = EncodingDetector.Resolve("utf-8").GetByteCount(decoded);return decoded;
    }

    private static bool ContainsHexEscape(string value,int start,int end)
    {
        for(var i=start;i<end;i++)
            if(value[i]=='\\'&&++i<end&&value[i]=='x')return true;
        return false;
    }

    private static void DecodeDefaultBytes(string value,int start,int end,Encoding encoding,StringBuilder result)
    {
        const int capacity=4096;
        var bytes=ArrayPool<byte>.Shared.Rent(capacity);
        var characters=ArrayPool<char>.Shared.Rent(encoding.GetMaxCharCount(capacity));
        var decoder=encoding.GetDecoder();var count=0;
        void Flush(bool final)
        {
            var written=decoder.GetChars(bytes,0,count,characters,0,final);
            result.Append(characters,0,written);count=0;
        }
        try
        {
            for(var i=start;i<end;i++)
            {
                byte next;
                if(value[i]!='\\')next=(byte)value[i];
                else
                {
                    if(++i>=end)throw new FormatException("ログ値末尾のescapeが不正です。");
                    if(value[i]=='x')
                    {
                        if(i+2>=end||!IsHex(value[i+1])||!IsHex(value[i+2]))
                            throw new FormatException("ログ値の \\xHH escapeが不正です。");
                        next=(byte)(Hex(value[i+1])*16+Hex(value[i+2]));
                        i+=2;
                    }
                    else next=(byte)DecodeEscape(value[i]);
                }
                // nginx can leave a multibyte character's ASCII trail byte plain
                // (CP932 ア is 83 41 => \\x83A). Decode the complete byte stream,
                // including plain ASCII and C escapes, rather than hex fragments.
                bytes[count++]=next;if(count==capacity)Flush(false);
            }
            Flush(true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);ArrayPool<char>.Shared.Return(characters);
        }
    }
    private static bool IsHex(char value)=>value is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f';
    private static int Hex(char value)=>value<='9'?value-'0':char.ToUpperInvariant(value)-'A'+10;
}
