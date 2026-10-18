using System.Buffers;
using System.Text;
using Microsoft.Data.Sqlite;

namespace WinCSVTool.Core;

public enum DecodeKind { Url, Base64 }

/// <summary>Strict text decoders. Bulk operations are bounded and roll back on the first invalid cell.</summary>
public sealed class DecodeService(Workspace workspace)
{
    /// <summary>URL ASCII and %HH form encoded byte runs; literal Unicode is preserved at strict byte-run boundaries.</summary>
    public static string DecodeText(string input, DecodeKind kind, string encodingName = "utf-8", bool plusAsSpace = true, CancellationToken token = default)
        => DecodeCore(input, kind, EncodingDetector.Resolve(encodingName), plusAsSpace, token);

    internal static string DecodeText(string input, DecodeKind kind, Encoding encoding, bool plusAsSpace = true, CancellationToken token = default)
        => DecodeCore(input, kind, encoding, plusAsSpace, token);

    internal static string FormatCellPosition(CsvDocument document, long ordinal, int column)
    {
        var sheet = string.IsNullOrEmpty(document.WorksheetName) ? "" : $"シート「{document.WorksheetName}」 / ";
        return $"{sheet}表示データ行 {ordinal + 1}（処理開始時の対象順） / 列 {document.Columns[column].HeaderName}";
    }

    private static string DecodeCore(string input, DecodeKind kind, Encoding encoding, bool plusAsSpace, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (kind == DecodeKind.Base64)
        {
            var result = encoding.GetString(Convert.FromBase64String(input));
            token.ThrowIfCancellationRequested();
            return result;
        }
        if (kind != DecodeKind.Url) throw new ArgumentOutOfRangeException(nameof(kind));
        var output = new StringBuilder(input.Length);
        for (var i = 0; i < input.Length;)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            if (input[i] > 127)
            {
                if (char.IsSurrogate(input[i]))
                {
                    if (!char.IsHighSurrogate(input[i]) || i + 1 >= input.Length || !char.IsLowSurrogate(input[i + 1]))
                        throw new FormatException($"URL文字列の Unicode が不正です（セル内の文字位置 {i + 1}）。");
                    output.Append(input[i++]);
                }
                output.Append(input[i++]);
                continue;
            }
            var start = i;
            var escaped = false;
            while (i < input.Length && input[i] <= 127)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                escaped |= input[i++] == '%';
            }
            // Text with no escapes is already decoded. This also preserves plain text
            // when UTF-16 was selected for the surrounding encoded byte runs.
            if (!escaped)
            {
                for (var index = start; index < i; index++)
                {
                    if ((index & 4095) == 0) token.ThrowIfCancellationRequested();
                    output.Append(input[index] == '+' && plusAsSpace ? ' ' : input[index]);
                }
                continue;
            }
            DecodeUrlRun(input, start, i, encoding, plusAsSpace, output, token);
        }
        token.ThrowIfCancellationRequested();
        return output.ToString();
    }

    private static void DecodeUrlRun(string input, int start, int end, Encoding encoding, bool plusAsSpace, StringBuilder output, CancellationToken token)
    {
        const int bufferSize = 4096;
        var bytes = ArrayPool<byte>.Shared.Rent(bufferSize);
        var characters = ArrayPool<char>.Shared.Rent(encoding.GetMaxCharCount(bufferSize));
        var decoder = encoding.GetDecoder();
        var count = 0;
        void Flush(bool final)
        {
            token.ThrowIfCancellationRequested();
            var written = decoder.GetChars(bytes, 0, count, characters, 0, final);
            output.Append(characters, 0, written);
            count = 0;
        }
        try
        {
            for (var i = start; i < end; i++)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                byte value;
                if (input[i] == '%')
                {
                    if (i + 2 >= end || !IsHex(input[i + 1]) || !IsHex(input[i + 2]))
                        throw new FormatException($"URLエンコードの %HH が不正です（セル内の文字位置 {i + 1}）。");
                    value = (byte)(Hex(input[i + 1]) * 16 + Hex(input[i + 2]));
                    i += 2;
                }
                else value = (byte)(input[i] == '+' && plusAsSpace ? ' ' : input[i]);
                bytes[count++] = value;
                if (count == bufferSize) Flush(false);
            }
            Flush(true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
            ArrayPool<char>.Shared.Return(characters);
        }
    }

    private static bool IsHex(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
    private static int Hex(char c) => c <= '9' ? c - '0' : char.ToUpperInvariant(c) - 'A' + 10;

    public long Apply(CsvDocument document, ViewSpec view, IReadOnlyList<int> columns, IReadOnlyCollection<long>? selectedRowIds,
        DecodeKind kind, string encodingName = "utf-8", bool plusAsSpace = true, CancellationToken token = default)
    {
        using var scope = selectedRowIds is null ? workspace.CaptureScope(document, view, token) : workspace.CaptureScope(document, selectedRowIds, token);
        return Apply(document, scope, columns, kind, encodingName, plusAsSpace, token);
    }

    public long Apply(CsvDocument document, RowScope scope, IReadOnlyList<int> columns,
        DecodeKind kind, string encodingName = "utf-8", bool plusAsSpace = true, CancellationToken token = default)
    {
        workspace.ValidateScope(document, scope);
        token.ThrowIfCancellationRequested();
        var selectedColumns = columns.Distinct().ToArray();
        if (selectedColumns.Any(c => c < 0 || c >= document.Columns.Count)) throw new ArgumentOutOfRangeException(nameof(columns));
        var encoding = EncodingDetector.Resolve(encodingName);
        if (kind is not (DecodeKind.Url or DecodeKind.Base64)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (selectedColumns.Length == 0 || scope.Count == 0) return 0;
        using var connection = workspace.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var cancellation = Workspace.ObserveCancellation(connection, token);
        long changed = 0;
        try
        {
            foreach (var col in selectedColumns)
            {
                var name = Workspace.Quote(document.Columns[col].Name); long cursor = -1;
                using var read = connection.CreateCommand(); read.Transaction = transaction;
                read.CommandText = $"SELECT s.ordinal,d.__id,d.{name} FROM {Workspace.Quote(scope.Table)} s JOIN {Workspace.Quote(document.TableName)} d ON d.__id=s.row_id WHERE s.ordinal>$after ORDER BY s.ordinal LIMIT 128";
                read.Parameters.Add("$after", SqliteType.Integer);
                using var update = connection.CreateCommand(); update.Transaction = transaction;
                update.CommandText = $"UPDATE {Workspace.Quote(document.TableName)} SET {name}=$value WHERE __id=$id";
                update.Parameters.Add("$value", SqliteType.Text); update.Parameters.Add("$id", SqliteType.Integer);
                while (true)
                {
                    token.ThrowIfCancellationRequested(); read.Parameters[0].Value = cursor;
                    var batch = new List<(long Ordinal, long Id, string Value)>(128);
                    long characters = 0;
                    using (var reader = read.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            token.ThrowIfCancellationRequested();
                            var value = reader.GetString(2);
                            // One full cell is unavoidable; do not retain 128 huge cells at once.
                            if (batch.Count > 0 && characters + value.Length > 4 * 1024 * 1024) break;
                            batch.Add((reader.GetInt64(0), reader.GetInt64(1), value));
                            characters += value.Length;
                        }
                    }
                    if (batch.Count == 0) break;
                    foreach (var (ordinal, id, value) in batch)
                    {
                        token.ThrowIfCancellationRequested(); cursor = ordinal;
                        if (value.Length == 0) continue;
                        string decoded;
                        try { decoded = DecodeCore(value, kind, encoding, plusAsSpace, token); }
                        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
                        { throw new FormatException($"{FormatCellPosition(document, ordinal, col)}: {ex.Message}\nデコードの変更はすべて取り消されました。", ex); }
                        if (decoded == value) continue;
                        update.Parameters[0].Value = decoded; update.Parameters[1].Value = id; update.ExecuteNonQuery(); changed++;
                    }
                }
            }
            token.ThrowIfCancellationRequested(); transaction.Commit();
        }
        catch (SqliteException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        // Dispose transaction and progress handler before invalidating indexes with a new connection.
        cancellation.Dispose(); transaction.Dispose();
        if (changed > 0) { document.IsDirty = true; workspace.InvalidateContentView(document); }
        return changed;
    }
}
