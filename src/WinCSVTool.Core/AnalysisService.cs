using System.Globalization;
using Microsoft.Data.Sqlite;

namespace WinCSVTool.Core;

public sealed record QueryResult(IReadOnlyList<string> Columns, IReadOnlyList<string[]> Rows, bool Truncated);
public sealed record StatisticsResult(IReadOnlyList<KeyValuePair<string, string>> Items);

/// <summary>SQL and exact, disk-backed statistics over the workspace's text-valued tables.</summary>
public sealed partial class AnalysisService(Workspace workspace)
{
    private const long PreviewCharacterBudget = 16 * 1024 * 1024;
    public QueryResult Query(string sql, int limit = 1000, CancellationToken token = default)
    {
        if (limit < 1 || limit > 100_000) throw new ArgumentOutOfRangeException(nameof(limit));
        ValidateQuery(sql);
        using var connection = workspace.OpenConnection(readOnly: true);
        PrepareQueryConnection(connection);
        using var cancellation = InterruptOnCancellation(connection, token);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        try
        {
            token.ThrowIfCancellationRequested();
            using var reader = command.ExecuteReader();
            var columns = ReadColumns(reader);
            var rows = new List<string[]>(Math.Min(limit, 1000));
            var truncated = false;
            long previewCharacters = 0;
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (rows.Count == limit) { truncated = true; break; }
                var values = ReadValues(reader);
                var rowCharacters = values.Sum(value => (long)value.Length + 16) + 16;
                // Keep complete cells. One exceptionally long row can exceed the budget,
                // but additional rows must not turn a bounded preview into a whole-file load.
                if (rows.Count > 0 && previewCharacters + rowCharacters > PreviewCharacterBudget) { truncated = true; break; }
                rows.Add(values);
                previewCharacters += rowCharacters;
            }
            return new QueryResult(columns, rows, truncated);
        }
        catch (SqliteException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
    }

    /// <summary>Streams every result row to a same-directory temporary file before replacing the destination.</summary>
    public long ExportQuery(string sql, string path, CsvOptions options, CancellationToken token = default)
    {
        ValidateQuery(sql);
        CsvCodec.ValidateDelimiter(options.Delimiter);
        using var connection = workspace.OpenConnection(readOnly: true);
        PrepareQueryConnection(connection);
        using var cancellation = InterruptOnCancellation(connection, token);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        try
        {
            token.ThrowIfCancellationRequested();
            using var output = new AtomicFile(path);
            long count = 0;
            using (var reader = command.ExecuteReader())
            using (var writer = new StreamWriter(output.Stream, EncodingDetector.Resolve(options.EncodingName.Equals("auto", StringComparison.OrdinalIgnoreCase) ? "utf-8" : options.EncodingName, options.WriteBom), 65536, true))
            {
                if (options.HasHeader) CsvCodec.WriteRecord(writer, ReadColumns(reader), options.Delimiter, options.EscapeSpreadsheetFormulas);
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    CsvCodec.WriteRecord(writer, ReadValues(reader), options.Delimiter, options.EscapeSpreadsheetFormulas);
                    count++;
                }
                token.ThrowIfCancellationRequested();
                writer.Flush();
            }
            output.Commit(token);
            return count;
        }
        catch (SqliteException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
    }

    /// <summary>Creates an editable document without holding the SQL result in memory.</summary>
    public CsvDocument CreateFromQuery(string sql, string name, CancellationToken token = default)
    {
        ValidateQuery(sql);
        using var source = workspace.OpenConnection(readOnly: true);
        PrepareQueryConnection(source);
        using var sourceCancellation = InterruptOnCancellation(source, token);
        using var query = source.CreateCommand();
        query.CommandText = sql;
        CsvDocument? document = null;
        try
        {
            token.ThrowIfCancellationRequested();
            using var reader = query.ExecuteReader();
            document = workspace.CreateDocument(name, ReadColumns(reader));
            using var target = workspace.OpenConnection();
            using var targetCancellation = InterruptOnCancellation(target, token);
            using var transaction = target.BeginTransaction();
            using var insert = target.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = $"INSERT INTO {Workspace.Quote(document.TableName)} ({string.Join(",", document.Columns.Select(c => Workspace.Quote(c.Name)))}) VALUES ({string.Join(",", document.Columns.Select((_, i) => "$v" + i))})";
            for (var i = 0; i < document.Columns.Count; i++) insert.Parameters.Add("$v" + i, SqliteType.Text);
            long count = 0;
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                for (var i = 0; i < reader.FieldCount; i++) insert.Parameters[i].Value = ReadValue(reader, i);
                insert.ExecuteNonQuery();
                count++;
            }
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            document.RowCount = count;
            document.IsDirty = true;
            workspace.InvalidateView(document);
            return document;
        }
        catch (Exception exception)
        {
            if (document is not null) workspace.CloseDocument(document);
            if (exception is SqliteException && token.IsCancellationRequested) throw new OperationCanceledException(token);
            throw;
        }
    }

    /// <summary>
    /// Computes the inclusive displayed-row interval. Empty means the exact empty string; whitespace is data.
    /// Exact distinct values, mode and interpolated quartiles use a disposable disk database.
    /// Numeric parsing is invariant and finite; text, including leading zeroes, is never rewritten.
    /// </summary>
    public StatisticsResult Stats(CsvDocument document, ViewSpec view, int column, long? startOffset = null, long? endOffset = null, CancellationToken token = default)
    {
        if (column < 0 || column >= document.Columns.Count) throw new ArgumentOutOfRangeException(nameof(column));
        var start = startOffset ?? 0;
        if (start < 0) throw new ArgumentOutOfRangeException(nameof(startOffset));
        if (endOffset is < 0 || endOffset < start) throw new ArgumentOutOfRangeException(nameof(endOffset));
        long? take = endOffset.HasValue ? checked(endOffset.Value - start + 1) : null;
        token.ThrowIfCancellationRequested();
        var idsSql = workspace.GetViewRangeSql(document, view, start, take, token);
        return StatsCore(document, column, idsSql, token);
    }

    public StatisticsResult StatsForRows(CsvDocument document, IEnumerable<long> selectedRowIds, int column, CancellationToken token = default)
    {
        using var scope = workspace.CaptureScope(document, selectedRowIds, token);
        return Stats(document, scope, column, token);
    }

    public StatisticsResult Stats(CsvDocument document, RowScope scope, int column, CancellationToken token = default)
        => StatsCore(document, column, workspace.GetScopeRowIdsSql(document, scope), token);

    private StatisticsResult StatsCore(CsvDocument document, int column, string idsSql, CancellationToken token)
    {
        if (column < 0 || column >= document.Columns.Count) throw new ArgumentOutOfRangeException(nameof(column));
        token.ThrowIfCancellationRequested();
        var scratchPath = Path.Combine(workspace.RootPath, $"statistics-{Guid.NewGuid():N}.sqlite");
        using var source = workspace.OpenConnection(readOnly: true);
        using var sourceCancellation = InterruptOnCancellation(source, token);
        using var read = source.CreateCommand();
        read.CommandText = $"SELECT {Workspace.Quote(document.Columns[column].Name)} FROM {Workspace.Quote(document.TableName)} WHERE __id IN ({idsSql})";
        try
        {
            using var scratch = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = scratchPath, Pooling = false }.ToString());
            scratch.Open();
            using var scratchCancellation = InterruptOnCancellation(scratch, token);
            Execute(scratch, "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF; PRAGMA cache_size=-4096; PRAGMA temp_store=FILE; CREATE TABLE samples (value TEXT NOT NULL, number REAL);");
            long rows = 0, nonempty = 0, numericCount = 0, timestampCount = 0, booleanCount = 0;
            long minLength = long.MaxValue, maxLength = 0;
            double meanLength = 0, mean = 0, m2 = 0, sum = 0, sumCompensation = 0;
            double numericMin = double.PositiveInfinity, numericMax = double.NegativeInfinity;
            string? textMin = null, textMax = null;
            DateTimeOffset? timeMin = null, timeMax = null;
            long trueCount = 0, falseCount = 0;
            using (var reader = read.ExecuteReader())
            using (var transaction = scratch.BeginTransaction())
            using (var insert = scratch.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO samples (value,number) VALUES ($v,$n)";
                var textParameter = insert.Parameters.Add("$v", SqliteType.Text);
                var numberParameter = insert.Parameters.Add("$n", SqliteType.Real);
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    var value = ReadValue(reader, 0);
                    rows++;
                    double? number = null;
                    if (value.Length != 0)
                    {
                        nonempty++;
                        var length = value.Length;
                        minLength = Math.Min(minLength, length);
                        maxLength = Math.Max(maxLength, length);
                        meanLength += (length - meanLength) / nonempty;
                        if (textMin is null || string.CompareOrdinal(value, textMin) < 0) textMin = value;
                        if (textMax is null || string.CompareOrdinal(value, textMax) > 0) textMax = value;
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedNumber) && double.IsFinite(parsedNumber))
                        {
                            number = parsedNumber;
                            numericCount++;
                            numericMin = Math.Min(numericMin, parsedNumber);
                            numericMax = Math.Max(numericMax, parsedNumber);
                            var delta = parsedNumber - mean;
                            mean += delta / numericCount;
                            m2 += delta * (parsedNumber - mean);
                            var compensatedValue = parsedNumber - sumCompensation;
                            var nextSum = sum + compensatedValue;
                            sumCompensation = (nextSum - sum) - compensatedValue;
                            sum = nextSum;
                        }
                        if (TryTimestamp(value, out var time))
                        {
                            timestampCount++;
                            if (timeMin is null || time < timeMin) timeMin = time;
                            if (timeMax is null || time > timeMax) timeMax = time;
                        }
                        if (TryBoolean(value, out var boolean))
                        {
                            booleanCount++;
                            if (boolean) trueCount++; else falseCount++;
                        }
                    }
                    textParameter.Value = value;
                    numberParameter.Value = number.HasValue ? number.Value : DBNull.Value;
                    insert.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            token.ThrowIfCancellationRequested();
            Execute(scratch, "CREATE INDEX sample_values ON samples(value COLLATE BINARY); CREATE INDEX sample_numbers ON samples(number) WHERE number IS NOT NULL;");
            var distinct = ScalarLong(scratch, "SELECT COUNT(*) FROM (SELECT value FROM samples WHERE value<>'' GROUP BY value COLLATE BINARY)");
            string mode = "—";
            long modeCount = 0;
            using (var modeCommand = scratch.CreateCommand())
            {
                modeCommand.CommandText = "SELECT value,COUNT(*) AS occurrences FROM samples WHERE value<>'' GROUP BY value COLLATE BINARY ORDER BY occurrences DESC,value COLLATE BINARY LIMIT 1";
                using var modeReader = modeCommand.ExecuteReader();
                if (modeReader.Read()) { mode = modeReader.GetString(0); modeCount = modeReader.GetInt64(1); }
            }
            var specifiedKind = document.Columns[column].Kind;
            var inferredKind = nonempty == 0 ? DataKind.Text : numericCount == nonempty ? DataKind.Number : booleanCount == nonempty ? DataKind.Boolean : timestampCount == nonempty ? DataKind.Timestamp : DataKind.Text;
            var effectiveKind = specifiedKind == DataKind.Auto ? inferredKind : specifiedKind;
            var items = new List<KeyValuePair<string, string>>();
            void Add(string key, object value) => items.Add(new(key, Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""));
            Add("列", document.Columns[column].Name);
            Add("データ型", KindName(effectiveKind) + (specifiedKind == DataKind.Auto ? " (自動判定)" : " (指定)"));
            Add("行数", rows); Add("空でない値", nonempty); Add("空の値", rows - nonempty);
            Add("異なる値", distinct); Add("最頻値", mode); Add("最頻値の件数", modeCount);
            Add("最頻値の同率時", "空の値を除外 / SQLite BINARY 順の先頭を表示");
            Add("文字列としての最小値", textMin ?? "—"); Add("文字列としての最大値", textMax ?? "—");
            Add("最小文字数", nonempty == 0 ? "—" : minLength); Add("最大文字数", nonempty == 0 ? "—" : maxLength);
            Add("平均文字数", nonempty == 0 ? "—" : Format(meanLength)); Add("文字数の単位", "UTF-16 コード単位 / 空の値を除外");
            Add("数値件数", numericCount); Add("数値でない値", nonempty - numericCount);
            if ((specifiedKind is DataKind.Auto or DataKind.Number) && numericCount > 0)
            {
                Add("数値統計の対象", "有限の数値として解析できる値 / 空・非数値は除外");
                Add("合計", Format(sum)); Add("平均", Format(mean)); Add("最小値", Format(numericMin)); Add("最大値", Format(numericMax));
                Add("分散 (母集団)", Format(m2 / numericCount)); Add("分散 (標本)", numericCount < 2 ? "—" : Format(m2 / (numericCount - 1)));
                Add("標準偏差 (母集団)", Format(Math.Sqrt(Math.Max(0, m2 / numericCount))));
                Add("標準偏差 (標本)", numericCount < 2 ? "—" : Format(Math.Sqrt(Math.Max(0, m2 / (numericCount - 1)))));
                var median = Quantile(scratch, numericCount, .5);
                var lowerQuartile = Quantile(scratch, numericCount, .25);
                var upperQuartile = Quantile(scratch, numericCount, .75);
                Add("中央値", Format(median)); Add("第1四分位数", Format(lowerQuartile)); Add("第3四分位数", Format(upperQuartile));
                Add("範囲", Format(numericMax - numericMin)); Add("四分位範囲", Format(upperQuartile - lowerQuartile));
                Add("四分位数の方式", "(件数 − 1) × 比率の位置を線形補間");
            }
            if (effectiveKind == DataKind.Timestamp)
            {
                Add("日時件数", timestampCount); Add("日時でない値", nonempty - timestampCount);
                Add("最小日時 (UTC)", timeMin?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "—");
                Add("最大日時 (UTC)", timeMax?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "—");
            }
            if (effectiveKind == DataKind.Boolean)
            {
                Add("真", trueCount); Add("偽", falseCount); Add("真偽値でない値", nonempty - booleanCount);
                Add("真の割合", booleanCount == 0 ? "—" : Format((double)trueCount / booleanCount));
            }
            token.ThrowIfCancellationRequested();
            return new StatisticsResult(items);
        }
        catch (SqliteException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        finally
        {
            if (File.Exists(scratchPath)) File.Delete(scratchPath);
            if (File.Exists(scratchPath + "-journal")) File.Delete(scratchPath + "-journal");
        }
    }

    private static string KindName(DataKind kind) => kind switch { DataKind.Number => "数値", DataKind.Timestamp => "日時", DataKind.Boolean => "真偽値", _ => "テキスト" };
    private static string Format(double value) => double.IsFinite(value) ? value.ToString("G17", CultureInfo.InvariantCulture) : "計算可能な数値範囲を超えました";
    private static bool TryTimestamp(string value, out DateTimeOffset result) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out result);
    private static bool TryBoolean(string value, out bool result)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "true" or "yes" or "1": result = true; return true;
            case "false" or "no" or "0": result = false; return true;
            default: result = false; return false;
        }
    }
    private static double Quantile(SqliteConnection connection, long count, double proportion)
    {
        var position = (count - 1) * proportion;
        var lowerIndex = (long)Math.Floor(position);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT number FROM samples WHERE number IS NOT NULL ORDER BY number LIMIT 2 OFFSET $offset";
        command.Parameters.AddWithValue("$offset", lowerIndex);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("統計対象が見つかりません。");
        var lower = reader.GetDouble(0);
        var fraction = position - lowerIndex;
        return fraction > 0 && reader.Read() ? lower * (1 - fraction) + reader.GetDouble(0) * fraction : lower;
    }
    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    private void PrepareQueryConnection(SqliteConnection connection)
    {
        // TEMP views shadow the physical tables so SELECT * returns only user CSV fields.
        // Main database is opened read-only; query_only is enabled after installing these views.
        foreach (var document in workspace.Documents)
        {
            var columns = string.Join(",", document.Columns.Select(column => Workspace.Quote(column.Name)));
            Execute(connection, $"CREATE TEMP VIEW {Workspace.Quote(document.TableName)} AS SELECT {columns} FROM main.{Workspace.Quote(document.TableName)}");
        }
        Execute(connection, "PRAGMA query_only=ON");
        // Qualified main-table reads must not bypass the user-column views or expose internal row IDs.
        SQLitePCL.raw.sqlite3_set_authorizer(connection.Handle!, (SQLitePCL.strdelegate_authorizer)((_, operation, table, column, _, _) =>
            operation == SQLitePCL.raw.SQLITE_READ && (string.Equals(column, "__id", StringComparison.OrdinalIgnoreCase) ||
                table?.StartsWith("map_", StringComparison.OrdinalIgnoreCase) == true || table?.StartsWith("scope_", StringComparison.OrdinalIgnoreCase) == true ||
                table?.StartsWith("__undo_", StringComparison.OrdinalIgnoreCase) == true ||
                table?.StartsWith("__wct_", StringComparison.OrdinalIgnoreCase) == true)
                ? SQLitePCL.raw.SQLITE_DENY : SQLitePCL.raw.SQLITE_OK), null);
    }
    private static IDisposable InterruptOnCancellation(SqliteConnection connection, CancellationToken token)
        => Workspace.ObserveCancellation(connection, token);
    private static string[] ReadColumns(SqliteDataReader reader) => Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
    private static string[] ReadValues(SqliteDataReader reader) => Enumerable.Range(0, reader.FieldCount).Select(i => ReadValue(reader, i)).ToArray();
    private static string ReadValue(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? "" : reader.GetValue(ordinal) is byte[] binary ? Convert.ToHexString(binary) : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? "";

    /// <summary>Lexes literals, quoted identifiers and comments before allowing exactly one read-only statement.</summary>
    private static void ValidateQuery(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) throw new ArgumentException("SELECT または WITH の SQL を入力してください。", nameof(sql));
        var tokens = new List<string>();
        var ended = false;
        for (var i = 0; i < sql.Length;)
        {
            var current = sql[i];
            if (char.IsWhiteSpace(current)) { i++; continue; }
            if (current == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            { i += 2; while (i < sql.Length && sql[i] != '\n' && sql[i] != '\r') i++; continue; }
            if (current == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var close = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close < 0) throw new ArgumentException("SQL のコメントが閉じられていません。", nameof(sql));
                i = close + 2; continue;
            }
            if (ended) throw new ArgumentException("SQL は SELECT または WITH の 1 文だけ実行できます。", nameof(sql));
            if (current == ';') { ended = true; i++; continue; }
            if (current is '\'' or '"' or '`' or '[')
            {
                var closeCharacter = current == '[' ? ']' : current;
                i++;
                var closed = false;
                while (i < sql.Length)
                {
                    if (sql[i++] != closeCharacter) continue;
                    if (current != '[' && i < sql.Length && sql[i] == closeCharacter) { i++; continue; }
                    closed = true; break;
                }
                if (!closed) throw new ArgumentException("SQL の引用符が閉じられていません。", nameof(sql));
                // A marker keeps a leading quoted SELECT from passing the statement check.
                if (tokens.Count == 0) tokens.Add("<quoted>");
                continue;
            }
            if (char.IsLetter(current) || current == '_')
            {
                var start = i++;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_')) i++;
                tokens.Add(sql[start..i].ToUpperInvariant());
            }
            else
            {
                if (tokens.Count == 0) tokens.Add("<symbol>");
                i++;
            }
        }
        if (tokens.Count == 0 || tokens[0] is not ("SELECT" or "WITH"))
            throw new ArgumentException("読み取り専用の SELECT または WITH を指定してください。", nameof(sql));
        string[] forbidden = ["INSERT", "UPDATE", "DELETE", "CREATE", "DROP", "ALTER", "ATTACH", "DETACH", "PRAGMA", "VACUUM", "REINDEX", "ANALYZE", "BEGIN", "COMMIT", "ROLLBACK", "SAVEPOINT", "RELEASE", "LOAD_EXTENSION", "READFILE", "WRITEFILE"];
        // REPLACE(...) is an ordinary string function. REPLACE INTO is a write statement.
        var replaceStatement = tokens.Zip(tokens.Skip(1)).Any(pair => pair.First == "REPLACE" && pair.Second == "INTO");
        if (replaceStatement || tokens.Any(token => forbidden.Contains(token, StringComparer.Ordinal)))
            throw new ArgumentException("更新、管理操作、ファイル操作を含む SQL は実行できません。", nameof(sql));
    }
}
