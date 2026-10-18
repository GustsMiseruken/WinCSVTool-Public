using System.Text.Json;
using WinCSVTool.Core;

namespace WinCSVTool.Cli;

internal sealed class Arguments
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Flags = ["no-header", "no-output-header", "bom", "regex", "literal-plus", "help", "version", "allow-lossy-xlsx-overwrite", "escape-spreadsheet-formulas"];
    private static readonly HashSet<string> Values = ["input", "out", "query", "query-file", "column", "kind", "decoded-encoding", "pattern", "replacement", "sort", "filter", "type", "sheet", "address", "asn", "country-language", "ip-data", "work-folder", "encoding", "output-encoding", "delimiter", "output-delimiter", "preview", "start", "settings"];
    public string Command { get; }
    public Arguments(string[] args)
    {
        Command = args.Length == 0 ? "help" : args[0].ToLowerInvariant();
        if (Command is "--help" or "-h") Command = "help";
        if (Command == "--version") Command = "version";
        for (var i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("オプションは --name value で指定してください: " + args[i]);
            var key = args[i][2..];
            if (!Flags.Contains(key) && !Values.Contains(key)) throw new ArgumentException("不明なオプション: --" + key);
            var value = "true";
            if (!Flags.Contains(key))
            {
                if (++i == args.Length || args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("値が必要です: --" + key);
                value = args[i];
            }
            if (!_values.TryGetValue(key, out var items)) _values[key] = items = [];
            if (items.Count > 0 && key is not ("input" or "column" or "sort" or "filter" or "type")) throw new ArgumentException("重複したオプション: --" + key);
            items.Add(value);
        }
    }
    public bool Has(string key) => _values.ContainsKey(key);
    public string? Get(string key) => _values.GetValueOrDefault(key)?.LastOrDefault();
    public string Required(string key) => Get(key) ?? throw new ArgumentException("--" + key + " が必要です。");
    public IReadOnlyList<string> All(string key) => _values.TryGetValue(key, out var items) ? items : [];
    public static char Delimiter(string? text, char fallback = ',') => text?.ToLowerInvariant() switch
    { null => fallback, "comma" or "," => ',', "tab" or "\\t" => '\t', "semicolon" or ";" => ';', _ => throw new ArgumentException("区切り文字は comma / tab / semicolon です。") };
}

internal sealed class CliDefaults
{
    public string WorkFolder { get; set; } = "";
    public string CountryLanguage { get; set; } = "ja";
    public string DefaultEncodingName { get; set; } = "auto";
    public string DefaultSaveEncodingName { get; set; } = "utf-8";
    public char DefaultDelimiter { get; set; } = ',';
    public bool DefaultHasHeader { get; set; } = true;
    public bool DefaultWriteBom { get; set; }
    public static CliDefaults Load(string? path)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, ".local", "settings.json");
        if (!File.Exists(path)) return new();
        using var input = File.OpenRead(path);
        if (input.Length > 1024 * 1024) throw new InvalidDataException("設定ファイルの上限は1MBです。");
        var result = JsonSerializer.Deserialize<CliDefaults>(input) ?? throw new InvalidDataException("設定ファイルが空です。");
        if (result.CountryLanguage is not ("ja" or "en")) result.CountryLanguage = "ja";
        if (result.DefaultDelimiter is not (',' or '\t' or ';')) result.DefaultDelimiter = ',';
        return result;
    }
    public CsvOptions ReadOptions(Arguments options) => new(options.Get("encoding") ?? DefaultEncodingName, !options.Has("no-header") && DefaultHasHeader, Arguments.Delimiter(options.Get("delimiter"), DefaultDelimiter));
    public CsvOptions WriteOptions(Arguments options, bool hasHeader = true) => new(options.Get("output-encoding") ?? DefaultSaveEncodingName,
        !options.Has("no-output-header") && hasHeader, Arguments.Delimiter(options.Get("output-delimiter"), DefaultDelimiter), options.Has("bom") || DefaultWriteBom,
        options.Has("escape-spreadsheet-formulas"));
}
