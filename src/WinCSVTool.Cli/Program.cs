using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using WinCSVTool.Core;

namespace WinCSVTool.Cli;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private sealed record InputTable(string Alias, CsvDocument Document);

    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try { return Run(new Arguments(args), cancellation.Token); }
        catch (OperationCanceledException) { Console.Error.WriteLine("処理を中断しました。"); return 130; }
        catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static int Run(Arguments args, CancellationToken token)
    {
        if (args.Command == "help" || args.Has("help")) { Console.WriteLine(Help); return 0; }
        if (args.Command == "version" || args.Has("version")) { Console.WriteLine(typeof(Program).Assembly.GetName().Version!.ToString(3)); return 0; }
        if (args.Command == "log-formats") { WriteJson(LogParserRegistry.Definitions, args.Get("out"), token); return 0; }
        string[] commands = ["sql", "convert", "info", "stats", "view", "find", "replace", "decode", "ip", "ip-lookup", "ip-data"];
        if (!commands.Contains(args.Command, StringComparer.Ordinal)) throw new ArgumentException("不明なコマンド: " + args.Command + "（helpで一覧を表示）");
        if (args.Get("out") is { } output && IsXlsx(output) && File.Exists(output) && !args.Has("allow-lossy-xlsx-overwrite") &&
            (args.Command is "convert" or "view" or "replace" or "decode" or "ip" || args.Command == "sql" && !args.Has("preview")))
            throw new ArgumentException("既存の XLSX への値のエクスポートでは数式・書式・グラフ等が失われます。別名で保存するか、--allow-lossy-xlsx-overwrite を明示してください。");
        var defaults = CliDefaults.Load(args.Get("settings"));
        var language = args.Get("country-language") ?? defaults.CountryLanguage;
        if (language is not ("ja" or "en")) throw new ArgumentException("--country-language は ja / en です。");
        if (args.Command is "ip-lookup" or "ip-data")
        {
            using var ip = new IpAnalysisService(args.Get("ip-data"));
            if (args.Command == "ip-data") WriteJson(ip.Metadata, args.Get("out"), token);
            else if (args.Get("address") is { } address && !args.Has("asn")) WriteJson(ip.Lookup(address, language), args.Get("out"), token);
            else if (args.Get("asn") is { } asn && !args.Has("address")) WriteJson(ip.LookupAsn(asn), args.Get("out"), token);
            else throw new ArgumentException("--address IP または --asn AS番号 のどちらかを指定してください。");
            return 0;
        }
        if (args.All("input").Count == 0) throw new ArgumentException("--input ファイル が必要です。");
        var folder = args.Get("work-folder") ?? defaults.WorkFolder;
        using var workspace = new Workspace(string.IsNullOrWhiteSpace(folder) ? null : folder);
        var tables = Import(workspace, args, defaults, token);
        var documents = tables.Select(t => t.Document).Distinct().ToArray();
        if(args.Get("out") is { } logOutput && documents.Any(d=>d.ImportSourcePath is { } source && string.Equals(Path.GetFullPath(source),Path.GetFullPath(logOutput),StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("アクセスログ原本には書き込めません。別名の出力先を指定してください。");
        if (args.Command == "info")
        {
            WriteJson(tables.Select(t => new { t.Alias, SqlTable = t.Document.TableName, File = t.Document.DisplayName, Sheet = t.Document.WorksheetName, Rows = t.Document.RowCount, Columns = t.Document.Columns.Select(c => new { c.Name, c.HeaderName, c.Kind }), t.Document.EncodingName, t.Document.HasHeader }), args.Get("out"), token); return 0;
        }
        var analysis = new AnalysisService(workspace);
        if (args.Command == "sql")
        {
            if (args.Has("query") == args.Has("query-file")) throw new ArgumentException("--query または --query-file のどちらかを指定してください。");
            var sql = args.Get("query") ?? File.ReadAllText(args.Required("query-file"), new UTF8Encoding(false, true));
            var write = defaults.WriteOptions(args);
            if (args.Get("preview") is { } preview)
            {
                if (!int.TryParse(preview, out var limit) || limit is < 1 or > 100000) throw new ArgumentException("--preview は1～100000行です。");
                WriteJson(analysis.Query(sql, limit, token), args.Get("out"), token);
            }
            else if (args.Get("out") is { } destination)
            {
                if (IsXlsx(destination))
                {
                    var result = analysis.CreateFromQuery(sql, "query", token);
                    new XlsxService(workspace).Export(destination, [result], write.HasHeader, token);
                }
                else analysis.ExportQuery(sql, destination, write, token);
            }
            else analysis.WriteQuery(sql, Console.Out, write.Delimiter, write.HasHeader, token, write.EscapeSpreadsheetFormulas);
            return 0;
        }
        if (args.Command == "convert")
        {
            Export(workspace, documents, new ViewSpec(), args, defaults, token); return 0;
        }
        if (documents.Length != 1) throw new ArgumentException("この操作は1つの表が対象です。--sheet でシートを選択するか、SQLを使用してください。");
        var document = documents[0];
        var view = BuildView(document, args);
        switch (args.Command)
        {
            case "stats":
                WriteJson(analysis.Stats(document, view, Column(document, args.Required("column")), token: token).Items, args.Get("out"), token); break;
            case "find":
                var start = 0L;
                if (args.Get("start") is { } offset && (!long.TryParse(offset, out start) || start < 0)) throw new ArgumentException("--start は0以上の表示行位置です。");
                var match = workspace.Find(document, view, args.Has("column") ? Column(document, args.Required("column")) : null, args.Required("pattern"), args.Has("regex"), start, token);
                WriteJson(match is null ? null : new { match.Column, match.Value, match.Offset }, args.Get("out"), token); break;
            case "replace":
            {
                using var scope = workspace.CaptureScope(document, view, token);
                var replaced = workspace.Replace(document, scope, args.Has("column") ? Column(document, args.Required("column")) : null, args.Required("pattern"), args.Required("replacement"), args.Has("regex"), token);
                Console.Error.WriteLine($"{replaced:N0} セルを置換しました。");
                Export(workspace, documents, view, args, defaults, token, scope); break;
            }
            case "decode":
            {
                var kind = args.Required("kind").ToLowerInvariant() switch { "url" => DecodeKind.Url, "base64" => DecodeKind.Base64, _ => throw new ArgumentException("--kind は url / base64 です。") };
                if (args.All("column").Count == 0) throw new ArgumentException("--column が必要です。複数指定できます。");
                using var scope = workspace.CaptureScope(document, view, token);
                new DecodeService(workspace).Apply(document, scope, args.All("column").Select(c => Column(document, c)).ToArray(), kind, args.Get("decoded-encoding") ?? "utf-8", !args.Has("literal-plus"), token);
                Export(workspace, documents, view, args, defaults, token, scope); break;
            }
            case "ip":
                using (var ip = new IpAnalysisService(args.Get("ip-data"))) workspace.EnrichIpAddresses(document, Column(document, args.Required("column")), ip, language, token);
                Export(workspace, documents, view, args, defaults, token); break;
            case "view": Export(workspace, documents, view, args, defaults, token); break;
        }
        return 0;
    }

    private static List<InputTable> Import(Workspace workspace, Arguments args, CliDefaults defaults, CancellationToken token)
    {
        var tables = new List<InputTable>();
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var specification in args.All("input"))
        {
            string? alias = null; var path = specification; string? inputLogFormat = null;
            var equals = specification.IndexOf('=');
            if (equals > 0)
            {
                var name=Regex.Match(specification[..equals],@"\A(?<alias>[A-Za-z_][A-Za-z0-9_]*)(?::(?<format>[A-Za-z0-9_-]+))?\z");
                if(name.Success){alias=name.Groups["alias"].Value;inputLogFormat=name.Groups["format"].Success?name.Groups["format"].Value:null;path=specification[(equals+1)..];}
            }
            var read = defaults.ReadOptions(args);
            var logFormat=inputLogFormat??args.Get("log-format");
            IReadOnlyList<CsvDocument> imported = logFormat is not null ? [workspace.ImportLog(path, LogOptions(args,read.EncodingName,logFormat),token)] : IsXlsx(path) ? new XlsxService(workspace).Import(path, read.HasHeader, token) : [workspace.Import(path, read, token)];
            if (args.Get("sheet") is { } sheet)
            {
                imported = int.TryParse(sheet, out var number) ? (number >= 1 && number <= imported.Count ? [imported[number - 1]] : []) : imported.Where(d => string.Equals(d.WorksheetName, sheet, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (imported.Count == 0) throw new ArgumentException("シートが見つかりません: " + sheet);
            }
            for (var i = 0; i < imported.Count; i++)
            {
                var document = imported[i];
                var name = alias is null ? document.TableName : i == 0 ? alias : alias + "_" + (i + 1).ToString(CultureInfo.InvariantCulture);
                if (!aliases.Add(name)) throw new ArgumentException("入力のSQL名が重複しています: " + name);
                tables.Add(new(name, document));
            }
        }
        using var connection = workspace.OpenConnection();
        foreach (var table in tables)
        {
            if (table.Alias == table.Document.TableName) continue;
            if (workspace.Documents.Any(d => string.Equals(d.TableName, table.Alias, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("SQL名は自動テーブル名と重複できません: " + table.Alias);
            using var command = connection.CreateCommand();
            command.CommandText = $"CREATE VIEW {Workspace.Quote(table.Alias)} AS SELECT {string.Join(',', table.Document.Columns.Select(c => Workspace.Quote(c.Name)))} FROM {Workspace.Quote(table.Document.TableName)}";
            command.ExecuteNonQuery();
        }
        return tables;
    }

    private static LogImportOptions LogOptions(Arguments args,string encoding,string format)
    {
        if(args.Has("log-pattern")&&args.Has("log-pattern-file"))throw new ArgumentException("--log-pattern と --log-pattern-file はどちらか一方を指定してください。");
        var pattern=args.Get("log-pattern");
        if(args.Get("log-pattern-file") is { } path){if(new FileInfo(path).Length>131072)throw new ArgumentException("ログ書式ファイルは128KiB以下にしてください。");pattern=File.ReadAllText(path,new UTF8Encoding(false,true)).TrimEnd('\r','\n');}
        var escape=args.Get("log-escape")??"default";
        if(!Enum.TryParse<LogEscapeMode>(escape,true,out var mode)||!Enum.IsDefined(mode))throw new ArgumentException("--log-escape は default / json / none です。");
        return new(format,pattern,encoding,mode);
    }

    private static int Column(CsvDocument document, string specification)
    {
        if (specification.StartsWith('#') && int.TryParse(specification.AsSpan(1), out var index) && index >= 1 && index <= document.Columns.Count) return index - 1;
        var found = document.Columns.Select((c, i) => (Column: c, Index: i)).Where(x => x.Column.HeaderName == specification || x.Column.Name == specification).Select(x => x.Index).Distinct().ToArray();
        return found.Length == 1 ? found[0] : throw new ArgumentException("列が見つからないか、名前が重複しています: " + specification + "（#1 のように列番号も指定できます）");
    }

    private static ViewSpec BuildView(CsvDocument document, Arguments args)
    {
        foreach (var type in args.All("type"))
        {
            var split = type.LastIndexOf(':');
            if (split < 1 || !Enum.TryParse<DataKind>(type[(split + 1)..], true, out var kind) || !Enum.IsDefined(kind)) throw new ArgumentException("--type は column:Number/Text/Timestamp/Boolean/Auto です。");
            document.Columns[Column(document, type[..split])].Kind = kind;
        }
        var view = new ViewSpec();
        foreach (var sort in args.All("sort"))
        {
            var split = sort.LastIndexOf(':'); var name = sort; var descending = false;
            if (split >= 0) { name = sort[..split]; descending = sort[(split + 1)..].ToLowerInvariant() switch { "desc" => true, "asc" => false, _ => throw new ArgumentException("--sort は column:asc または column:desc です。") }; }
            view.Sorts.Add(new(Column(document, name), descending));
        }
        foreach (var filter in args.All("filter"))
        {
            var parts = filter.Split(':', 3);
            if (parts.Length != 3 || !Enum.TryParse<FilterOperator>(parts[1], true, out var op) || !Enum.IsDefined(op)) throw new ArgumentException("--filter は column:Equals:value などです。");
            view.Filters.Add(new(Column(document, parts[0]), op, parts[2]));
        }
        return view;
    }

    private static void Export(Workspace workspace, IReadOnlyList<CsvDocument> documents, ViewSpec view, Arguments args, CliDefaults defaults, CancellationToken token, RowScope? scope = null)
    {
        var write = defaults.WriteOptions(args, documents.All(d => d.HasHeader));
        if (args.Get("out") is { } path && IsXlsx(path))
        {
            using var captured = scope is null && !view.IsEmpty ? workspace.CaptureScope(documents.Single(), view, token) : null;
            new XlsxService(workspace).Export(path, documents, write.HasHeader, token, scope ?? captured);
        }
        else
        {
            if (documents.Count != 1) throw new ArgumentException("複数の表を保存する場合は --out book.xlsx を指定してください。");
            if (args.Get("out") is { } csvPath)
            {
                if (scope is null) workspace.Export(documents[0], csvPath, write, view, token);
                else workspace.Export(documents[0], csvPath, write, scope, token);
            }
            else
            {
                if (write.HasHeader) CsvCodec.WriteRecord(Console.Out, documents[0].Columns.Select(c => c.HeaderName), write.Delimiter, write.EscapeSpreadsheetFormulas);
                var rows = scope is null ? workspace.EnumerateRows(documents[0], view, token) : workspace.EnumerateRows(documents[0], scope, token);
                foreach (var row in rows) CsvCodec.WriteRecord(Console.Out, row.Values, write.Delimiter, write.EscapeSpreadsheetFormulas);
                Console.Out.Flush();
            }
        }
    }

    private static bool IsXlsx(string path) => Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase);
    private static void WriteJson<T>(T value, string? path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var json = JsonSerializer.Serialize(value, JsonOptions);
        token.ThrowIfCancellationRequested();
        if (path is null) { Console.WriteLine(json); return; }
        using var output = new AtomicFile(path);
        using (var writer = new StreamWriter(output.Stream, new UTF8Encoding(false), 65536, true)) writer.Write(json);
        output.Commit(token);
    }

    private const string Help = """
WinCSVTool.Cli — CSV / XLSX / SQL / オフラインIP解析

  sql       --input name=file.csv [--input other=file.xlsx] --query "SELECT * FROM name" [--out result.csv]
  convert   --input one.csv [--input two.csv] --out book.xlsx
  info      --input book.xlsx
  stats     --input file.csv --column amount
  view      --input file.csv --sort amount:desc --type amount:Number --filter status:Equals:200 --out result.csv
  find      --input file.csv --pattern text [--regex] [--column name]
  replace   --input file.csv --pattern text --replacement new [--regex] --out result.csv
  decode    --input file.csv --column url --kind url [--decoded-encoding utf-8] --out result.csv
  ip        --input file.csv --column ip --country-language ja --out enriched.csv
  ip-lookup --address 8.8.8.8 [--country-language en]
  ip-lookup --asn AS15169
  ip-data
  log-formats

gzip: CSV.gz / TSV.gz の読込と保存に対応。--out result.csv.gz、SQLの--outも利用可能。
アクセスログ: --log-format nginx/apache/tomcat/jboss/aws-waf/cloudflare-waf
  --log-pattern 書式本体 または --log-pattern-file UTF8ファイル、--log-escape default/json/none
  nginxは$変数、他のテキスト形式は%変数を入力（設定ディレクティブ全体ではなく書式本体）。common/combinedも利用可。
  JSONLの列設定は timestamp,httpRequest.clientIp,$ のようなカンマ区切り。$は原JSONを保持。
  混在SQL入力: --input logs:nginx=access.log.gz --input lookup=lookup.csv
  不正行は行位置を示して取込全体を中止。ログ原本への出力は禁止、別名CSV/CSV.gzを指定。

入力: --encoding auto/utf-8/shift_jis/... --delimiter comma/tab/semicolon --no-header --sheet 名前または1からの番号
出力: --output-encoding utf-8/... --output-delimiter comma/tab/semicolon --no-output-header --bom
Excel向けCSV: --escape-spreadsheet-formulas は式として解釈され得る値に先頭アポストロフィを追加（値が変わります）。通常出力は値を保持します。
既存XLSXへの値の出力: --allow-lossy-xlsx-overwrite を明示。数式・書式・グラフ等は保持されません。別名保存を推奨します。
SQL: --query-file UTF8ファイル または --query、--preview 1000 は上限付きJSON出力
列名: ヘッダー名・SQL列名、または #1 のような1からの列番号。--sort / --filter / --type / --column は繰り返し可。
共通: --work-folder パス --ip-data 参照データのフォルダー --settings JSONファイル
出力先省略時: 表は全行をUTF-8 CSV、info/stats/find/IP情報はJSONとして標準出力へ出力します。
findのOffsetは0からの表示行位置、Columnは0からの列位置です。内部行IDは出力しません。
Ctrl+Cで中断。終了コード: 0=成功、1=処理失敗、2=引数エラー、130=中断。
Excelの書式や数式の再計算は保存しません。CSVの先頭ゼロは文字列として保持します。
""";
}
