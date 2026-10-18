using System.Text;

namespace WinCSVTool.Core;

/// <summary>Conservative BOM/Unicode detection followed by Japanese legacy encodings.</summary>
public static class EncodingDetector
{
    static EncodingDetector() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static Encoding Resolve(string name, bool writeBom = false)
    {
        name = name.Trim().ToLowerInvariant().Replace("_", "-");
        return name switch
        {
            "utf8" or "utf-8" => new UTF8Encoding(writeBom, true),
            "utf16" or "utf-16" or "utf-16le" or "unicode" => new UnicodeEncoding(false, writeBom, true),
            "utf-16be" or "unicodefffe" => new UnicodeEncoding(true, writeBom, true),
            "cp932" or "shift-jis" or "shiftjis" or "sjis" or "windows-31j" =>
                Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
            "euc-jp" or "eucjp" =>
                Encoding.GetEncoding(51932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
            _ => throw new ArgumentException($"未対応の文字コードです: {name}", nameof(name))
        };
    }

    public static string Detect(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var sample = new byte[(int)Math.Min(64 * 1024, stream.Length)];
        var length = stream.Read(sample, 0, sample.Length);
        var bytes = sample.AsSpan(0, length);
        if (bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) return "utf-8";
        if (bytes.StartsWith(new byte[] { 0xff, 0xfe })) return "utf-16le";
        if (bytes.StartsWith(new byte[] { 0xfe, 0xff })) return "utf-16be";

        // A BOM-less UTF-16 file containing Latin CSV delimiters commonly has alternating zeros.
        if (length >= 8)
        {
            var evenZeros = 0;
            var oddZeros = 0;
            for (var i = 0; i < length; i++)
                if (bytes[i] == 0) { if ((i & 1) == 0) evenZeros++; else oddZeros++; }
            if (oddZeros > length / 8 && evenZeros < oddZeros / 4) return "utf-16le";
            if (evenZeros > length / 8 && oddZeros < evenZeros / 4) return "utf-16be";
        }
        if (CanDecode(bytes, Resolve("utf-8"), length == stream.Length)) return "utf-8";
        if (CanDecode(bytes, Resolve("cp932"), length == stream.Length)) return "cp932";
        if (CanDecode(bytes, Resolve("euc-jp"), length == stream.Length)) return "euc-jp";
        throw new DecoderFallbackException("文字コードを判定できません。文字コードを指定して開いてください。");
    }

    private static bool CanDecode(ReadOnlySpan<byte> bytes, Encoding encoding, bool complete)
    {
        try
        {
            // Do not reject a multibyte character split at the end of the detection sample.
            encoding.GetDecoder().GetCharCount(bytes, complete);
            return true;
        }
        catch (DecoderFallbackException) { return false; }
    }
}
