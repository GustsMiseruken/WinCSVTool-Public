using System.Collections.Concurrent;

namespace WinCSVTool.Core;

public enum LogEscapeMode { Default, Json, None }
public sealed record LogImportOptions(string ParserId = "nginx", string? Pattern = null, string EncodingName = "auto", LogEscapeMode EscapeMode = LogEscapeMode.Default);
public sealed record LogParserDefinition(string Id, string Name, string DefaultPattern, bool IsJson);
public interface ILogRecordParser
{
    IReadOnlyList<string> Headers { get; }
    string[] Parse(string line, CancellationToken token = default);
}

/// <summary>Registered factories create a parser per import; extensions do not share mutable row state.</summary>
public static class LogParserRegistry
{
    private sealed record Registration(LogParserDefinition Definition, Func<LogImportOptions, ILogRecordParser> Factory);
    private static readonly ConcurrentDictionary<string, Registration> Parsers = new(StringComparer.OrdinalIgnoreCase);
    static LogParserRegistry()
    {
        const string combined = "%h %l %u %t \"%r\" %s %b \"%{Referer}i\" \"%{User-Agent}i\"";
        Add(new("nginx", "nginx log_format", "$remote_addr - $remote_user [$time_local] \"$request\" $status $body_bytes_sent \"$http_referer\" \"$http_user_agent\"",false));
        foreach(var (id,name) in new[]{("apache","Apache LogFormat"),("tomcat","Tomcat AccessLogValve")})Add(new(id,name,combined,false));
        Add(new("jboss","JBoss / Undertow access log","%h %l %u %t \"%r\" %s %b \"%{i,Referer}\" \"%{i,User-Agent}\"",false));
        Add(new("aws-waf","AWS WAF JSONL","timestamp,httpRequest.clientIp,httpRequest.country,httpRequest.httpMethod,httpRequest.uri,httpRequest.args,httpRequest.httpVersion,httpRequest.headers,action,terminatingRuleId,$",true));
        Add(new("cloudflare-waf","Cloudflare WAF / Logpush JSONL","Datetime,ClientIP,ClientCountry,ClientRequestMethod,ClientRequestPath,ClientRequestQuery,ClientRequestURI,UserAgent,ClientRequestUserAgent,Action,Source,RuleID,RayID,EdgeResponseStatus,$",true));
    }
    private static void Add(LogParserDefinition definition)=>Register(definition,options=>definition.IsJson?new JsonLogParser(options.Pattern??definition.DefaultPattern):new PatternLogParser(definition.Id,options.Pattern??definition.DefaultPattern,options.EscapeMode,options.EncodingName));
    public static IReadOnlyList<LogParserDefinition> Definitions=>Parsers.Values.Select(p=>p.Definition).OrderBy(p=>p.Id,StringComparer.Ordinal).ToArray();
    public static void Register(LogParserDefinition definition,Func<LogImportOptions,ILogRecordParser> factory)
    {
        ArgumentNullException.ThrowIfNull(definition);ArgumentNullException.ThrowIfNull(factory);
        if(string.IsNullOrWhiteSpace(definition.Id)||string.IsNullOrWhiteSpace(definition.Name))throw new ArgumentException("パーサーIDと名前が必要です。");
        if(!Parsers.TryAdd(definition.Id,new(definition,factory)))throw new ArgumentException("パーサーIDが重複しています: "+definition.Id);
    }
    public static ILogRecordParser Create(LogImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if(!Enum.IsDefined(options.EscapeMode))throw new ArgumentException("ログのescape方式が不正です。");
        if(!Parsers.TryGetValue(options.ParserId,out var parser))throw new ArgumentException("ログ形式が見つかりません: "+options.ParserId);
        var result=parser.Factory(options);
        if(result.Headers.Count is <1 or >1999 || result.Headers.Any(string.IsNullOrWhiteSpace))throw new ArgumentException("ログの列数は1～1,999、列名は空欄以外にしてください。");
        return result;
    }
}
