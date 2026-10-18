using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using MaxMind.Db;
using Microsoft.Data.Sqlite;

namespace WinCSVTool.Core;

public enum IpAddressKind { Public, Private, Loopback, LinkLocal, Shared, Documentation, Multicast, Unspecified, Reserved, Invalid, Unknown }
public sealed record IpLookupResult(string Address, string CountryCode, string CountryName, string AddressClass, long? Asn,
    string Organization, string SnapshotDate, string Source, string License, IpAddressKind Kind);
public sealed record AsnLookupResult(long? Asn, string Organization, bool Found, string SnapshotDate, string Source, string License);
public sealed record IpDataMetadata
{
    public string SnapshotDate { get; init; } = "";
    public string Source { get; init; } = "";
    public string CountrySource { get; init; } = "";
    public string AsnSource { get; init; } = "";
    public string License { get; init; } = "CC BY 4.0";
    public string Attribution { get; init; } = "IP Geolocation and ASN data by DB-IP.com";
    public string CountryFile { get; init; } = "";
    public string AsnFile { get; init; } = "";
    public string AsnIndexFile { get; init; } = "";
    public string CountryBuildUtc { get; init; } = "";
    public string AsnBuildUtc { get; init; } = "";
    public string CountrySha256 { get; init; } = "";
    public string AsnSha256 { get; init; } = "";
    public string AsnIndexSha256 { get; init; } = "";
    public long AsnOrganizations { get; init; }
}

/// <summary>Local, memory-mapped IP reference lookup. No DNS, HTTP, or other network calls are made.</summary>
public sealed class IpAnalysisService : IDisposable
{
    private readonly Reader country;
    private readonly Reader asn;
    private readonly SqliteConnection asnIndex;
    private readonly SqliteCommand asnQuery;
    private readonly object gate = new();
    private readonly Dictionary<string, IpLookupResult> cache = new(StringComparer.Ordinal);
    private readonly Queue<string> cacheOrder = new();
    private const int CacheCapacity = 8192;
    private bool disposed;
    public IpDataMetadata Metadata { get; }
    public string DataDirectory { get; }

    public IpAnalysisService(string? dataDirectory = null)
    {
        DataDirectory = Path.GetFullPath(dataDirectory ?? Path.Combine(AppContext.BaseDirectory, "data", "ip"));
        Metadata = JsonSerializer.Deserialize<IpDataMetadata>(File.ReadAllText(Path.Combine(DataDirectory, "metadata.json")))
            ?? throw new InvalidDataException("IP 参照データの metadata.json が空です。");
        if (!DateOnly.TryParseExact(Metadata.SnapshotDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new InvalidDataException("IP 参照データの基準日が不正です。");
        country = new Reader(DataPath(Metadata.CountryFile), FileAccessMode.MemoryMapped);
        try
        {
            asn = new Reader(DataPath(Metadata.AsnFile), FileAccessMode.MemoryMapped);
            try
            {
                asnIndex = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = DataPath(Metadata.AsnIndexFile), Mode = SqliteOpenMode.ReadOnly, Pooling = false
                }.ToString());
                try
                {
                    asnIndex.Open();
                    using var limits = asnIndex.CreateCommand();
                    limits.CommandText = "PRAGMA cache_size=-256; PRAGMA query_only=ON; PRAGMA mmap_size=0;";
                    limits.ExecuteNonQuery();
                    asnQuery = asnIndex.CreateCommand();
                    asnQuery.CommandText = "SELECT organization FROM asn_organizations WHERE asn=$asn";
                    asnQuery.Parameters.Add("$asn", SqliteType.Integer);
                    asnQuery.Prepare();
                }
                catch { asnIndex.Dispose(); throw; }
            }
            catch { asn.Dispose(); throw; }
        }
        catch { country.Dispose(); throw; }
    }

    private string DataPath(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name)
            throw new InvalidDataException("IP 参照データのファイル名が不正です。");
        var path = Path.Combine(DataDirectory, name);
        if (!File.Exists(path)) throw new FileNotFoundException("IP 参照データが見つかりません。配布 ZIP をフォルダーごと展開してください。", path);
        return path;
    }

    public IpLookupResult Lookup(string? input, string language = "ja")
    {
        var japanese = IsJapanese(language);
        var value = input?.Trim() ?? "";
        var cacheable = value.Length <= 64;
        var key = cacheable ? (japanese ? "ja:" : "en:") + value : "";
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (cacheable && cache.TryGetValue(key, out var existing)) return existing;
            var result = LookupCore(value, japanese);
            // Extremely long malformed cells never enter the cache.
            if (cacheable)
            {
                if (cache.Count >= CacheCapacity) cache.Remove(cacheOrder.Dequeue());
                cache[key] = result;
                cacheOrder.Enqueue(key);
            }
            return result;
        }
    }

    private IpLookupResult LookupCore(string value, bool japanese)
    {
        if (!TryParseAddress(value, out var address))
            return Result(value, "", "", IpAddressKind.Invalid, null, "", japanese);
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var kind = Classify(address);
        if (kind != IpAddressKind.Public)
            return Result(address.ToString(), "", ClassName(kind, japanese), kind, null, "", japanese);
        var details = country.Find<IpCountryRecord>(address)?.Country;
        var asnRecord = asn.Find<IpAsnRecord>(address);
        var code = details?.Code ?? "";
        var name = (japanese ? details?.Names?.Japanese : details?.Names?.English) ?? details?.Names?.English ?? code;
        long? number = asnRecord?.Number;
        if (number == 0) number = null;
        var organization = asnRecord?.Organization ?? "";
        if (code is "ZZ" or "--") code = name = "";
        if (code.Length == 0 && number is null) kind = IpAddressKind.Unknown;
        return Result(address.ToString(), code, name, kind, number, organization, japanese);
    }

    private IpLookupResult Result(string address, string code, string name, IpAddressKind kind, long? number, string organization, bool japanese) =>
        new(address, code, name, ClassName(kind, japanese), number, organization, Metadata.SnapshotDate,
            Metadata.Source, Metadata.License, kind);

    public AsnLookupResult LookupAsn(string? value)
    {
        var text = value?.Trim() ?? "";
        if (text.StartsWith("AS", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? LookupAsn(number) : new(null, "", false, Metadata.SnapshotDate, Metadata.AsnSource, Metadata.License);
    }

    public AsnLookupResult LookupAsn(long number)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (number is <= 0 or > uint.MaxValue) return new(null, "", false, Metadata.SnapshotDate, Metadata.AsnSource, Metadata.License);
            asnQuery.Parameters[0].Value = number;
            var organization = asnQuery.ExecuteScalar() as string;
            return new(number, organization ?? "", organization is not null, Metadata.SnapshotDate, Metadata.AsnSource, Metadata.License);
        }
    }

    public static bool IsJapanese(string language) => language.ToLowerInvariant() switch
    {
        "ja" or "ja-jp" or "japanese" => true,
        "en" or "en-us" or "english" => false,
        _ => throw new ArgumentException("国名の言語は ja または en を指定してください。", nameof(language))
    };

    public static string ClassName(IpAddressKind kind, bool japanese) => (kind, japanese) switch
    {
        (IpAddressKind.Public, true) => "公開IP", (IpAddressKind.Public, false) => "Public IP",
        (IpAddressKind.Private, true) => "プライベートIP", (IpAddressKind.Private, false) => "Private IP",
        (IpAddressKind.Loopback, true) => "ループバック", (IpAddressKind.Loopback, false) => "Loopback",
        (IpAddressKind.LinkLocal, true) => "リンクローカル", (IpAddressKind.LinkLocal, false) => "Link-local",
        (IpAddressKind.Shared, true) => "共有アドレス（CGNAT）", (IpAddressKind.Shared, false) => "Shared address (CGNAT)",
        (IpAddressKind.Documentation, true) => "文書用アドレス", (IpAddressKind.Documentation, false) => "Documentation",
        (IpAddressKind.Multicast, true) => "マルチキャスト", (IpAddressKind.Multicast, false) => "Multicast",
        (IpAddressKind.Unspecified, true) => "未指定", (IpAddressKind.Unspecified, false) => "Unspecified",
        (IpAddressKind.Reserved, true) => "予約・特殊用途", (IpAddressKind.Reserved, false) => "Reserved / special use",
        (IpAddressKind.Invalid, true) => "不正なIP", (IpAddressKind.Invalid, false) => "Invalid IP",
        (_, true) => "不明", _ => "Unknown"
    };

    private static bool TryParseAddress(string value, out IPAddress address)
    {
        address = IPAddress.None;
        if (value.Length is 0 or > 64) return false;
        if (value.StartsWith('['))
        {
            if (!value.EndsWith(']')) return false;
            value = value[1..^1];
        }
        if (value.Contains('[') || value.Contains(']')) return false;
        if (value.Contains('.'))
        {
            var parts = value[(value.LastIndexOf(':') + 1)..].Split('.');
            if (parts.Length != 4 || parts.Any(p => p.Length is 0 or > 3 || (p.Length > 1 && p[0] == '0') ||
                p.Any(c => c is < '0' or > '9') || !byte.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _))) return false;
        }
        else if (!value.Contains(':')) return false;
        return IPAddress.TryParse(value, out address!);
    }

    private static IpAddressKind Classify(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return IpAddressKind.Loopback;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var a = bytes[0]; var b = bytes[1]; var c = bytes[2]; var d = bytes[3];
            if (a == 0) return IpAddressKind.Unspecified;
            if (a == 10 || a == 172 && b is >= 16 and <= 31 || a == 192 && b == 168) return IpAddressKind.Private;
            if (a == 169 && b == 254) return IpAddressKind.LinkLocal;
            if (a == 100 && b is >= 64 and <= 127) return IpAddressKind.Shared;
            if (a == 192 && b == 0 && c == 2 || a == 198 && b == 51 && c == 100 || a == 203 && b == 0 && c == 113) return IpAddressKind.Documentation;
            if (a is >= 224 and <= 239) return IpAddressKind.Multicast;
            if (a >= 240 || a == 198 && b is 18 or 19 || a == 192 && b == 88 && c == 99 ||
                a == 192 && b == 0 && c == 0 && d is not (9 or 10)) return IpAddressKind.Reserved;
            return IpAddressKind.Public;
        }
        if (address.Equals(IPAddress.IPv6Any)) return IpAddressKind.Unspecified;
        if ((bytes[0] & 0xfe) == 0xfc) return IpAddressKind.Private;
        if (address.IsIPv6LinkLocal) return IpAddressKind.LinkLocal;
        if (address.IsIPv6Multicast) return IpAddressKind.Multicast;
        if (Prefix(bytes, "2001:db8::", 32) || Prefix(bytes, "3fff::", 20)) return IpAddressKind.Documentation;
        if (Prefix(bytes, "64:ff9b:1::", 48) || Prefix(bytes, "100::", 64) || Prefix(bytes, "100:0:0:1::", 64) ||
            Prefix(bytes, "2001:2::", 48) || Prefix(bytes, "5f00::", 16) || address.IsIPv6SiteLocal) return IpAddressKind.Reserved;
        if ((bytes[0] & 0xe0) != 0x20 && !Prefix(bytes, "64:ff9b::", 96)) return IpAddressKind.Reserved;
        return IpAddressKind.Public;
    }

    private static bool Prefix(byte[] address, string network, int bits)
    {
        var prefix = IPAddress.Parse(network).GetAddressBytes();
        var whole = bits / 8; var remainder = bits % 8;
        return address.AsSpan(0, whole).SequenceEqual(prefix.AsSpan(0, whole)) &&
            (remainder == 0 || (address[whole] >> (8 - remainder)) == (prefix[whole] >> (8 - remainder)));
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            asnQuery.Dispose(); asnIndex.Dispose(); asn.Dispose(); country.Dispose();
            cache.Clear(); cacheOrder.Clear();
        }
    }
}

// Only the requested language fields are decoded; complete multilingual maps are not materialized per lookup.
internal sealed class IpCountryRecord
{
    [MapKey("country")] public IpCountryDetails? Country { get; init; }
}
internal sealed class IpCountryDetails
{
    [MapKey("iso_code")] public string? Code { get; init; }
    [MapKey("names")] public IpCountryNames? Names { get; init; }
}
internal sealed class IpCountryNames
{
    [MapKey("ja")] public string? Japanese { get; init; }
    [MapKey("en")] public string? English { get; init; }
}
internal sealed class IpAsnRecord
{
    [MapKey("autonomous_system_number")] public long? Number { get; init; }
    [MapKey("autonomous_system_organization")] public string? Organization { get; init; }
}
