using System.Text.RegularExpressions;
using System.Text;

namespace WinCSVTool.Core;

public sealed record BulkChangePreview(long Rows, int Columns, long ChangedCells, IReadOnlyList<string> Examples);

public sealed partial class Workspace
{
    public BulkChangePreview PreviewReplace(CsvDocument document, ViewSpec view, int? column, string search,
        string replacement, bool regex, CancellationToken token = default)
    {
        var pattern = PreviewReplacePattern(document, column, search, regex, token);
        using var scope = CaptureScope(document, view, token);
        return PreviewReplaceCore(document, scope, column, search, replacement, pattern, token);
    }

    public BulkChangePreview PreviewReplace(CsvDocument document, RowScope scope, int? column, string search,
        string replacement, bool regex, CancellationToken token = default) =>
        PreviewReplaceCore(document, scope, column, search, replacement, PreviewReplacePattern(document, column, search, regex, token), token);

    private static Regex? PreviewReplacePattern(CsvDocument document, int? column, string search, bool regex, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (column.HasValue) ValidateColumn(document, column.Value);
        if (!regex && search.Length == 0) throw new ArgumentException("検索文字列を入力してください。", nameof(search));
        return regex ? new Regex(search, RegexOptions.CultureInvariant, RegexTimeout) : null;
    }

    private BulkChangePreview PreviewReplaceCore(CsvDocument document, RowScope scope, int? column, string search,
        string replacement, Regex? pattern, CancellationToken token) =>
        PreviewChanges(document, scope, column.HasValue ? [column.Value] : Enumerable.Range(0, document.Columns.Count).ToArray(),
            value => pattern is null ? value.Replace(search, replacement, StringComparison.OrdinalIgnoreCase) : pattern.Replace(value, replacement), false, token);

    public BulkChangePreview PreviewDecode(CsvDocument document, ViewSpec view, IReadOnlyList<int> columns,
        IReadOnlyCollection<long>? selectedIds, DecodeKind kind, string encodingName, bool plusAsSpace, CancellationToken token = default)
    {
        var selectedColumns = PreviewColumns(document, columns, token);
        var encoding = PreviewDecodeEncoding(kind, encodingName, token);
        using var scope = selectedIds is null ? CaptureScope(document, view, token) : CaptureScope(document, selectedIds, token);
        return PreviewDecodeCore(document, scope, selectedColumns, kind, encoding, plusAsSpace, token);
    }

    public BulkChangePreview PreviewDecode(CsvDocument document, RowScope scope, IReadOnlyList<int> columns,
        DecodeKind kind, string encodingName, bool plusAsSpace, CancellationToken token = default)
    {
        var selectedColumns = PreviewColumns(document, columns, token);
        var encoding = PreviewDecodeEncoding(kind, encodingName, token);
        return PreviewDecodeCore(document, scope, selectedColumns, kind, encoding, plusAsSpace, token);
    }

    private static int[] PreviewColumns(CsvDocument document, IReadOnlyList<int> columns, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var selected = new HashSet<int>();
        var result = new List<int>();
        foreach (var column in columns)
        {
            token.ThrowIfCancellationRequested();
            ValidateColumn(document, column);
            if (selected.Add(column)) result.Add(column);
        }
        return result.ToArray();
    }

    private static Encoding PreviewDecodeEncoding(DecodeKind kind, string encodingName, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (kind is not (DecodeKind.Url or DecodeKind.Base64)) throw new ArgumentOutOfRangeException(nameof(kind));
        return EncodingDetector.Resolve(encodingName);
    }

    private BulkChangePreview PreviewDecodeCore(CsvDocument document, RowScope scope, IReadOnlyList<int> columns,
        DecodeKind kind, Encoding encoding, bool plusAsSpace, CancellationToken token) =>
        PreviewChanges(document, scope, columns,
            value => value.Length == 0 ? value : DecodeService.DecodeText(value, kind, encoding, plusAsSpace, token), true, token);

    private BulkChangePreview PreviewChanges(CsvDocument document, RowScope scope, IReadOnlyList<int> columns,
        Func<string, string> transform, bool decoding, CancellationToken token)
    {
        ValidateScope(document, scope);
        token.ThrowIfCancellationRequested();
        var examples = new List<string>(5); long rows = 0, changes = 0;
        using var connection = OpenConnection(true);
        using var cancellation = ObserveCancellation(connection, token);
        using var command = connection.CreateCommand();
        // Read only the chosen cells, one at a time. IDs and display order stay in the shared disk scope.
        var projection = string.Concat(columns.Select(column => ",d." + Quote(document.Columns[column].Name)));
        command.CommandText = $"SELECT s.ordinal{projection} FROM {Quote(scope.Table)} s JOIN {Quote(document.TableName)} d ON d.__id=s.row_id ORDER BY s.ordinal";
        using var reader = ReadCommand(command, token);
        while (ReadNext(reader, token))
        {
            token.ThrowIfCancellationRequested(); rows++;
            for (var i = 0; i < columns.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var column = columns[i];
                var original = reader.GetString(i + 1);
                string changed;
                try { changed = transform(original); }
                catch (RegexMatchTimeoutException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
                catch (Exception ex) when (decoding && (ex is FormatException or DecoderFallbackException))
                {
                    throw new FormatException($"{DecodeService.FormatCellPosition(document, reader.GetInt64(0), column)}: {ex.Message}\nデコードの変更は行っていません。", ex);
                }
                if (original == changed) continue;
                changes++;
                if (examples.Count < 5) examples.Add(Abbreviate(document.Columns[column].HeaderName) + ": " + Abbreviate(original) + " → " + Abbreviate(changed));
            }
        }
        return new(rows, columns.Count, changes, examples);
    }

    private static string Abbreviate(string value)
    {
        var shortValue = value.Length > 80 ? value[..80] + "…" : value;
        return shortValue.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
    }
}
