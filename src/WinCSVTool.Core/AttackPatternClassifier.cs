using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace WinCSVTool.Core;

[Flags]
public enum AttackCategory { None = 0, Sqli = 1, Xss = 2, Traversal = 4, Lfi = 8, Rce = 16, Ssrf = 32, Scan = 64 }
public sealed record AttackClassifierOptions(int DecodePasses = 2, int MaximumInputCharacters = 65_536);
public sealed record AttackClassification(AttackCategory Categories, string Reasons, bool Incomplete, int DecodePasses);

/// <summary>Bounded offline heuristics. A match is a rule indication, not proof of a successful attack.</summary>
public static class AttackPatternClassifier
{
    private static readonly (AttackCategory Category, string Name)[] CategoryNames = [(AttackCategory.Sqli, "SQLi"), (AttackCategory.Xss, "XSS"), (AttackCategory.Traversal, "Path Traversal"), (AttackCategory.Lfi, "LFI"), (AttackCategory.Rce, "RCE"), (AttackCategory.Ssrf, "SSRF"), (AttackCategory.Scan, "Scan")];
    private sealed record Rule(AttackCategory Category, string Reason, Regex Pattern);
    private static Rule Make(AttackCategory category, string reason, string pattern) => new(category, reason,
        new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50)));
    private static readonly Regex SqlExecutableComments = Make(AttackCategory.Sqli, "", @"/\*![0-9]{0,6}\s*([\s\S]*?)\*/").Pattern;
    private static readonly Regex SqlComments = Make(AttackCategory.Sqli, "", @"/\*[\s\S]*?\*/").Pattern;
    private static readonly Regex HttpTargets = Make(AttackCategory.Ssrf, "", @"\bhttps?://[^\s<>""'\\]+").Pattern;
    private static readonly Regex EncodingMarkers = Make(AttackCategory.None, "", @"%[0-9a-f]{2}|%u[0-9a-f]{4}|\\[ux][0-9a-f]{2}|&(?:#[0-9]+|#x[0-9a-f]+|[a-z]+);").Pattern;
    private static readonly Rule[] Rules = [
        Make(AttackCategory.Sqli, "SQL keyword / tautology pattern", @"\bunion\s+(?:all\s+)?select\b|\bselect\b.{0,120}\bfrom\b|\b(?:or|and)\s+['""0-9]+\s*=\s*['""0-9]+|\b(?:sleep|benchmark)\s*\("),
        Make(AttackCategory.Xss, "HTML script / event / JavaScript URI pattern", @"<\s*script\b|\bon(?:error|load|click|mouseover)\s*=|javascript\s*:|<\s*(?:svg|iframe)\b"),
        Make(AttackCategory.Traversal, "Parent-directory path pattern", @"\.\.[/\\]"),
        Make(AttackCategory.Lfi, "Local-file / PHP stream pattern", @"/etc/(?:passwd|shadow)|[/\\]windows[/\\]win\.ini|php://(?:filter|input)|file://"),
        Make(AttackCategory.Rce, "Shell command / substitution pattern", @"(?:;|\|{1,2}|&&|\$\(|`)\s*(?:cat|curl|wget|bash|sh|cmd|powershell|whoami|id)\b|\b(?:cmd\.exe|powershell(?:\.exe)?)\s*(?:/c|-c|-enc)"),
        Make(AttackCategory.Ssrf, "Non-HTTP fetch pattern", @"(?:gopher|dict)://"),
        Make(AttackCategory.Scan, "Common probe path / scanner identifier", @"(?:/\.env\b|/\.git/|/wp-(?:admin|login)|/phpmyadmin|/server-status|\b(?:sqlmap|nikto|nmap|masscan)\b)") ];

    public static void Validate(AttackClassifierOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.DecodePasses is < 0 or > 3 || options.MaximumInputCharacters is < 256 or > 1_048_576) throw new ArgumentException("デコード回数は0～3、解析文字数は256～1,048,576で指定してください。", nameof(options));
    }
    public static string FormatCategories(AttackCategory categories)
    {
        if ((categories & ~(AttackCategory.Sqli | AttackCategory.Xss | AttackCategory.Traversal | AttackCategory.Lfi | AttackCategory.Rce | AttackCategory.Ssrf | AttackCategory.Scan)) != AttackCategory.None)
            throw new ArgumentOutOfRangeException(nameof(categories));
        if (categories == AttackCategory.None) return "一致なし";
        return string.Join(", ", CategoryNames.Where(item => (categories & item.Category) != 0).Select(item => item.Name));
    }
    public static AttackClassification Classify(string? raw, AttackClassifierOptions? options = null)
    {
        options ??= new(); Validate(options); raw ??= "";
        var incomplete = raw.Length > options.MaximumInputCharacters; var text = raw[..Math.Min(raw.Length, options.MaximumInputCharacters)];
        var categories = AttackCategory.None; var reasons = new HashSet<string>(StringComparer.Ordinal); var passes = 0;
        for (var pass = 0; pass <= options.DecodePasses; pass++)
        {
            foreach (var rule in Rules)
                try
                {
                    // SQL comments separate tokens; executable MySQL comments retain their body.
                    var inspected = rule.Category == AttackCategory.Sqli && text.Contains("/*", StringComparison.Ordinal)
                        ? SqlComments.Replace(SqlExecutableComments.Replace(text, " $1 "), " ") : text;
                    if (rule.Pattern.IsMatch(inspected)) { categories |= rule.Category; reasons.Add(rule.Reason); }
                }
                catch (RegexMatchTimeoutException) { incomplete = true; reasons.Add("Regex time limit reached"); }
            try
            {
                foreach (Match target in HttpTargets.Matches(text))
                {
                    if (target.Length > 4096) { incomplete = true; reasons.Add("URL inspection length limit reached"); continue; }
                    if (Uri.TryCreate(target.Value.TrimEnd(',', ';', ')', '}'), UriKind.Absolute, out var uri) && IsInternalHost(uri.DnsSafeHost))
                    { categories |= AttackCategory.Ssrf; reasons.Add("Internal-address HTTP fetch pattern"); }
                }
                if (pass == options.DecodePasses)
                {
                    if (EncodingMarkers.IsMatch(text)) { incomplete = true; reasons.Add("Decoding pass limit reached"); }
                    break;
                }
            }
            catch (RegexMatchTimeoutException) { incomplete = true; reasons.Add("Regex time limit reached"); if (pass == options.DecodePasses) break; }
            var decoded = DecodeEscapes(text, ref incomplete, reasons);
            try { decoded = DecodeService.DecodeText(decoded, DecodeKind.Url); }
            catch (Exception error) when (error is FormatException or DecoderFallbackException)
            {
                incomplete = true; reasons.Add("Invalid URL encoding; inspection is partial");
                try { decoded = Uri.UnescapeDataString(decoded.Replace('+', ' ')); }
                catch (UriFormatException) { reasons.Add("URL decoding failed"); }
            }
            decoded = WebUtility.HtmlDecode(decoded);
            if (decoded.Length > options.MaximumInputCharacters) { incomplete = true; decoded = decoded[..options.MaximumInputCharacters]; }
            if (decoded == text) break; text = decoded; passes++;
        }
        if (incomplete) reasons.Add("Incomplete inspection; original text retained");
        return new(categories, string.Join("; ", reasons), incomplete, passes);
    }

    private static bool IsInternalHost(string host)
    {
        host = host.TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return true;
        // Numeric parsing only: no DNS, network or server-side request is performed.
        if (!IPAddress.TryParse(host, out var address)) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return bytes[0] is 0 or 10 or 127 || bytes[0] == 169 && bytes[1] == 254 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168;
        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || (bytes[0] & 0xfe) == 0xfc;
    }

    private static string DecodeEscapes(string text, ref bool incomplete, HashSet<string> reasons)
    {
        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (i + 1 < text.Length && (text[i] == '\\' && text[i + 1] is 'u' or 'x' || text[i] == '%' && text[i + 1] == 'u'))
            {
                var digits = text[i + 1] == 'u' ? 4 : 2;
                if (i + digits + 1 >= text.Length || !ushort.TryParse(text.AsSpan(i + 2, digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
                { incomplete = true; reasons.Add("Invalid character escape; inspection is partial"); result.Append(text[i]); continue; }
                var character = (char)code;
                if (char.IsSurrogate(character))
                {
                    var next = i + digits + 2;
                    if (!char.IsHighSurrogate(character) || next + 5 >= text.Length || text[next] != text[i] || text[next + 1] != 'u' ||
                        !ushort.TryParse(text.AsSpan(next + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var low) || !char.IsLowSurrogate((char)low))
                    { incomplete = true; reasons.Add("Invalid Unicode escape; inspection is partial"); result.Append(text[i]); continue; }
                    result.Append(character).Append((char)low); i = next + 5; continue;
                }
                result.Append(character); i += digits + 1;
            }
            else if (i + 1 < text.Length && text[i] == '\\' && text[i + 1] == '\\') { result.Append('\\'); i++; }
            else result.Append(text[i]);
        }
        return result.ToString();
    }
}
