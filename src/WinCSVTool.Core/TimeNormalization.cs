using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WinCSVTool.Core;

public enum TimeInputKind { Auto, UnixSeconds, UnixMilliseconds, Iso8601, Apache, Custom }
public enum LogTimeZone { Utc, Jst }
public enum TimeOutputKind { Iso8601, UnixSeconds, UnixMilliseconds }
public sealed record TimeParseOptions(TimeInputKind InputKind = TimeInputKind.Auto, LogTimeZone AssumeTimeZone = LogTimeZone.Jst,
    LogTimeZone OutputTimeZone = LogTimeZone.Utc, string? CustomFormat = null, TimeOutputKind OutputKind = TimeOutputKind.Iso8601);

/// <summary>Deterministic log timestamp conversion; offset-free values never use the machine's local zone.</summary>
public static class TimeNormalization
{
    private static readonly Regex OffsetSuffix = new("(?:[zZ]|[+-][0-9]{2}:?[0-9]{2})$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50));
    private static readonly Regex CompactOffset = new("([+-][0-9]{2})([0-9]{2})$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50));
    private static readonly string[] LocalFormats = ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd"];
    private static readonly string[] OffsetFormats = ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mmzzz", "yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd HH:mm:sszzz", "yyyy-MM-dd HH:mmzzz"];

    public static void Validate(TimeParseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.InputKind) || !Enum.IsDefined(options.AssumeTimeZone) || !Enum.IsDefined(options.OutputTimeZone) || !Enum.IsDefined(options.OutputKind)) throw new ArgumentException("時刻形式・タイムゾーンが不正です。", nameof(options));
        if (options.InputKind == TimeInputKind.Custom && (string.IsNullOrWhiteSpace(options.CustomFormat) || options.CustomFormat.Length > 128 || !options.CustomFormat.Contains('y') || !options.CustomFormat.Contains('M') || !options.CustomFormat.Contains('d')))
            throw new ArgumentException("日時書式は年月日を含む128文字以内の.NETカスタム書式で指定してください。", nameof(options));
        if (options.InputKind == TimeInputKind.Custom)
            try { _ = DateTimeOffset.UnixEpoch.ToString(options.CustomFormat, CultureInfo.InvariantCulture); }
            catch (FormatException ex) { throw new ArgumentException("日時のカスタム書式が不正です。", nameof(options), ex); }
    }

    public static bool TryParse(string? value, TimeParseOptions options, out DateTimeOffset instant)
    {
        Validate(options);
        if (!TryParseCore(value, options, out instant)) return false;
        try { if (options.OutputKind == TimeOutputKind.Iso8601) _ = instant.ToOffset(Offset(options.OutputTimeZone)); return true; }
        catch (ArgumentOutOfRangeException) { instant = default; return false; }
    }

    private static bool TryParseCore(string? value, TimeParseOptions options, out DateTimeOffset instant)
    {
        instant = default;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024) return false;
        var text = value.Trim();
        try
        {
            if (options.InputKind is TimeInputKind.UnixSeconds or TimeInputKind.UnixMilliseconds || options.InputKind == TimeInputKind.Auto && decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _))
            {
                if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var epoch)) return false;
                var milliseconds = options.InputKind == TimeInputKind.UnixMilliseconds || options.InputKind == TimeInputKind.Auto && Math.Abs(epoch) >= 100_000_000_000m;
                var ticks = checked((long)decimal.Truncate(epoch * (milliseconds ? TimeSpan.TicksPerMillisecond : TimeSpan.TicksPerSecond)));
                instant = DateTimeOffset.UnixEpoch.AddTicks(ticks); return true;
            }
            if (options.InputKind is TimeInputKind.Apache or TimeInputKind.Auto)
            {
                var apache = CompactOffset.Replace(text.Trim('[', ']'), "$1:$2");
                if (DateTimeOffset.TryParseExact(apache, "dd/MMM/yyyy:HH:mm:ss zzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out instant)) return true;
                if (options.InputKind == TimeInputKind.Apache) return false;
            }
            if (options.InputKind == TimeInputKind.Custom)
            {
                var requiredOffsetFormat = CustomOffsetFormat(options.CustomFormat!, "zzz", out var hasOffset, out var hasOptionalOffset);
                if (hasOffset)
                    return DateTimeOffset.TryParseExact(text, options.CustomFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out instant);
                if (hasOptionalOffset)
                {
                    if (DateTimeOffset.TryParseExact(text, requiredOffsetFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out instant)) return true;
                    var utcFormat = CustomOffsetFormat(options.CustomFormat!, "'Z'", out _, out _);
                    if (DateTimeOffset.TryParseExact(text, utcFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out instant)) return true;
                    var missingOffsetFormat = CustomOffsetFormat(options.CustomFormat!, "", out _, out _);
                    if (!DateTime.TryParseExact(text, missingOffsetFormat, CultureInfo.InvariantCulture, DateTimeStyles.NoCurrentDateDefault, out var withoutOffset)) return false;
                    instant = new DateTimeOffset(DateTime.SpecifyKind(withoutOffset, DateTimeKind.Unspecified), Offset(options.AssumeTimeZone)); return true;
                }
                var literalSuffix = OffsetSuffix.Match(text);
                var literalZulu = literalSuffix.Value == "Z" && literalSuffix.Index > 0 && char.IsAsciiDigit(text[literalSuffix.Index - 1]);
                if (literalSuffix.Length > 1 || literalZulu)
                {
                    if (!DateTimeOffset.TryParseExact(text, options.CustomFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out instant)) return false;
                    var suffix = OffsetSuffix.Match(text).Value;
                    var offset = suffix.Equals("Z", StringComparison.OrdinalIgnoreCase) ? TimeSpan.Zero : TimeSpan.ParseExact(CompactOffset.Replace(suffix, "$1:$2")[1..], @"hh\:mm", CultureInfo.InvariantCulture) * (suffix[0] == '-' ? -1 : 1);
                    instant = new DateTimeOffset(instant.DateTime, offset); return true;
                }
                if (!DateTime.TryParseExact(text, options.CustomFormat, CultureInfo.InvariantCulture, DateTimeStyles.NoCurrentDateDefault, out var custom)) return false;
                instant = new DateTimeOffset(DateTime.SpecifyKind(custom, DateTimeKind.Unspecified), Offset(options.AssumeTimeZone)); return true;
            }
            var decimalPoint = text.IndexOf('.');
            if (decimalPoint >= 0 && (decimalPoint + 1 == text.Length || !char.IsAsciiDigit(text[decimalPoint + 1]))) return false;
            if (OffsetSuffix.IsMatch(text))
            {
                text = CompactOffset.Replace(text, "$1:$2"); if (text.EndsWith('Z') || text.EndsWith('z')) text = text[..^1] + "+00:00";
                return DateTimeOffset.TryParseExact(text, OffsetFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out instant);
            }
            if (!DateTime.TryParseExact(text, LocalFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) return false;
            instant = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Offset(options.AssumeTimeZone)); return true;
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or OverflowException or FormatException or RegexMatchTimeoutException) { instant = default; return false; }
    }

    // z/K inside literals or after a backslash are ordinary text, not timezone fields.
    // Replacing optional K lets us distinguish a supplied offset from its empty form
    // without ever allowing DateTime's parser to convert through the host timezone.
    private static string CustomOffsetFormat(string format, string optionalReplacement, out bool hasOffset, out bool hasOptionalOffset)
    {
        var result = new StringBuilder(format.Length); var quote = '\0'; hasOffset = false; hasOptionalOffset = false;
        for (var i = 0; i < format.Length; i++)
        {
            var ch = format[i];
            if (ch == '\\') { result.Append(ch); if (i + 1 < format.Length) result.Append(format[++i]); continue; }
            if (quote != '\0') { result.Append(ch); if (ch == quote) quote = '\0'; continue; }
            if (ch is '\'' or '"') { quote = ch; result.Append(ch); continue; }
            if (ch == '%' && i + 1 < format.Length)
            {
                var token = format[++i];
                if (token == 'K') { hasOptionalOffset = true; result.Append(optionalReplacement); }
                else { if (token == 'z') hasOffset = true; result.Append('%').Append(token); }
                continue;
            }
            if (ch == 'z') hasOffset = true;
            if (ch == 'K') { hasOptionalOffset = true; result.Append(optionalReplacement); }
            else result.Append(ch);
        }
        return result.ToString();
    }

    public static string Format(DateTimeOffset value, LogTimeZone zone)
        => Format(value, zone, TimeOutputKind.Iso8601);

    public static string Format(DateTimeOffset value, LogTimeZone zone, TimeOutputKind outputKind)
    {
        if (!Enum.IsDefined(zone)) throw new ArgumentOutOfRangeException(nameof(zone));
        if (!Enum.IsDefined(outputKind)) throw new ArgumentOutOfRangeException(nameof(outputKind));
        if (outputKind == TimeOutputKind.UnixSeconds) return ((value.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / (decimal)TimeSpan.TicksPerSecond).ToString("0.#######", CultureInfo.InvariantCulture);
        if (outputKind == TimeOutputKind.UnixMilliseconds) return value.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var fraction = value.Ticks % TimeSpan.TicksPerMillisecond == 0 ? "fff" : "fffffff";
        return value.ToOffset(Offset(zone)).ToString("yyyy-MM-dd'T'HH:mm:ss." + fraction + "zzz", CultureInfo.InvariantCulture);
    }
    private static TimeSpan Offset(LogTimeZone zone) => zone == LogTimeZone.Jst ? TimeSpan.FromHours(9) : TimeSpan.Zero;
}
