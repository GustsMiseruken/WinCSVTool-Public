using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace WinCSVTool.Core;

public sealed partial class Workspace
{
    private readonly Dictionary<string, TimeSeriesResult> logSeriesCache = new(StringComparer.Ordinal);
    private bool logCachesCleaned;

    private void CleanupRecoveredLogCaches(SqliteConnection connection)
    {
        if (logCachesCleaned) return;
        using var command = connection.CreateCommand(); command.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
        var stale = new List<string>();
        using (var reader = command.ExecuteReader()) while (reader.Read())
        {
            var name = reader.GetString(0);
            if (System.Text.RegularExpressions.Regex.IsMatch(name, "^__log_(rows|series)_[0-9a-f]{32}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)) stale.Add(name);
        }
        using var transaction = connection.BeginTransaction(); command.Transaction = transaction;
        foreach (var name in stale) { command.CommandText = $"DROP TABLE {Quote(name)}"; command.ExecuteNonQuery(); }
        transaction.Commit(); logCachesCleaned = true;
    }

    public LogTransformSummary AddNormalizedTimeColumn(CsvDocument document, int column, TimeParseOptions options,
        string header = "正規化時刻", CancellationToken token = default, IProgress<ImportProgress>? progress = null)
    {
        TimeNormalization.Validate(options); ArgumentException.ThrowIfNullOrWhiteSpace(header);
        return AddLogColumns(document, [column], [header], values =>
        {
            if (!TimeNormalization.TryParse(values[0], options, out var instant)) return ([""], true, false);
            return ([TimeNormalization.Format(instant, options.OutputTimeZone, options.OutputKind)], false, false);
        }, token, progress, options.OutputKind == TimeOutputKind.Iso8601 ? DataKind.Timestamp : DataKind.Number);
    }

    public LogTransformSummary AddAttackClassificationColumns(CsvDocument document, IReadOnlyList<int> sourceColumns,
        AttackClassifierOptions? options = null, CancellationToken token = default, IProgress<ImportProgress>? progress = null)
    {
        options ??= new(); AttackPatternClassifier.Validate(options);
        return AddLogColumns(document, sourceColumns, ["攻撃分類（ルール判定）", "判定理由", "判定状態"], values =>
        {
            var category = AttackCategory.None; var reasons = new HashSet<string>(StringComparer.Ordinal); var incomplete = false;
            foreach (var value in values)
            {
                var result = AttackPatternClassifier.Classify(value, options); category |= result.Categories; incomplete |= result.Incomplete;
                if (result.Reasons.Length != 0) reasons.Add(result.Reasons);
            }
            return ([AttackPatternClassifier.FormatCategories(category), string.Join("; ", reasons), incomplete ? "解析上限あり（要確認）" : category == AttackCategory.None ? "未検出（安全性未確認）" : "ルール判定（要確認）"], incomplete, category != AttackCategory.None);
        }, token, progress, sourceCharacterLimit: options.MaximumInputCharacters);
    }

    private LogTransformSummary AddLogColumns(CsvDocument document, IReadOnlyList<int> sourceColumns, string[] headers,
        Func<string[], (string[] Values, bool Invalid, bool Matched)> project, CancellationToken token, IProgress<ImportProgress>? progress, DataKind kind = DataKind.Text, int sourceCharacterLimit = 1024)
    {
        ArgumentNullException.ThrowIfNull(sourceColumns); token.ThrowIfCancellationRequested();
        lock (gate)
        {
            ValidateColumnStructureDocument(document);
            var sources = sourceColumns.Distinct().ToArray();
            if (sources.Length == 0) throw new ArgumentException("解析対象の列を選択してください。", nameof(sourceColumns));
            foreach (var index in sources) ValidateColumn(document, index);
            if (document.Columns.Count + headers.Length > 1999) throw new InvalidDataException("解析列を追加すると上限1,999列を超えます。");
            try
            {
                using var connection = OpenConnection(); using var transaction = connection.BeginTransaction();
                using var cancellation = ObserveCancellation(connection, token);
                DropUndoTables(connection, transaction, undoEntry);
                var columns = new List<CsvColumn>(); var used = document.Columns.Select(c => c.Name).ToList();
                foreach (var header in headers)
                {
                    var name = UniqueName(header, used); used.Add(name);
                    using var add = connection.CreateCommand(); add.Transaction = transaction;
                    add.CommandText = $"ALTER TABLE {Quote(document.TableName)} ADD COLUMN {Quote(name)} TEXT NOT NULL DEFAULT ''"; ExecuteCommand(add, token);
                    columns.Add(new CsvColumn(name) { HeaderName = header, Kind = kind });
                }
                using var select = connection.CreateCommand(); select.Transaction = transaction;
                select.CommandText = $"SELECT __id,{string.Join(',', sources.Select(i => $"substr({Quote(document.Columns[i].Name)},1,{sourceCharacterLimit + 1})"))} FROM {Quote(document.TableName)} WHERE __id>$after ORDER BY __id LIMIT 512";
                select.Parameters.Add("$after", SqliteType.Integer); select.Prepare();
                using var update = connection.CreateCommand(); update.Transaction = transaction;
                update.CommandText = $"UPDATE {Quote(document.TableName)} SET {string.Join(',', columns.Select((c, i) => Quote(c.Name) + "=$v" + i))} WHERE __id=$id";
                for (var i = 0; i < columns.Count; i++) update.Parameters.Add("$v" + i, SqliteType.Text);
                update.Parameters.Add("$id", SqliteType.Integer); update.Prepare();
                long after = 0, processed = 0, invalid = 0, matched = 0;
                progress?.Report(new(0, 0, document.RowCount));
                while (true)
                {
                    token.ThrowIfCancellationRequested(); select.Parameters[0].Value = after;
                    var batch = new List<(long Id, string[] Values)>(512); long characters = 0;
                    using (var reader = ReadCommand(select, token)) while (ReadNext(reader, token))
                    {
                        var values = new string[sources.Length]; for (var i = 0; i < values.Length; i++) { values[i] = reader.IsDBNull(i + 1) ? "" : reader.GetString(i + 1); characters += values[i].Length; }
                        batch.Add((reader.GetInt64(0), values)); if (characters >= 4 * 1024 * 1024) break;
                    }
                    if (batch.Count == 0) break;
                    foreach (var row in batch)
                    {
                        token.ThrowIfCancellationRequested(); var result = project(row.Values);
                        for (var i = 0; i < columns.Count; i++) update.Parameters[i].Value = result.Values[i];
                        update.Parameters[columns.Count].Value = row.Id; ExecuteCommand(update, token);
                        processed++; if (result.Invalid) invalid++; if (result.Matched) matched++; after = row.Id;
                    }
                    progress?.Report(new(processed, processed, document.RowCount));
                }
                DropDocumentMaps(connection, transaction, document, token); WriteRecoveryCheckpoint(connection, transaction, document, columns);
                token.ThrowIfCancellationRequested(); transaction.Commit();
                document.Columns.AddRange(columns); document.IsDirty = true; maps.Remove(document.TableName); undoEntry = null;
                return new(processed, invalid, matched);
            }
            catch (SqliteException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        }
    }

    private static string LogSchema(CsvDocument document) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document.Columns.Select(c => new { c.Name, c.HeaderName, c.Kind })))));
    private static string RevisionTrigger(CsvDocument document, string operation) => "__wct_lr_" + document.TableName + "_" + operation;
    private static long LogRevision(SqliteConnection connection, CsvDocument document, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT revision FROM __wct_log_revision WHERE document=$name"; command.Parameters.AddWithValue("$name", document.TableName);
        return Convert.ToInt64(command.ExecuteScalar() ?? -1, CultureInfo.InvariantCulture);
    }
    private static bool HasLogRevisionTriggers(SqliteConnection connection, CsvDocument document)
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name IN ($i,$u,$d)";
        command.Parameters.AddWithValue("$i", RevisionTrigger(document, "insert")); command.Parameters.AddWithValue("$u", RevisionTrigger(document, "update")); command.Parameters.AddWithValue("$d", RevisionTrigger(document, "delete"));
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 3;
    }
    private static void EnsureLogRevision(SqliteConnection connection, CsvDocument document)
    {
        using var transaction = connection.BeginTransaction(); using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "CREATE TABLE IF NOT EXISTS __wct_log_revision(document TEXT PRIMARY KEY,revision INTEGER NOT NULL); INSERT OR IGNORE INTO __wct_log_revision VALUES($name,0)";
        command.Parameters.AddWithValue("$name", document.TableName); command.ExecuteNonQuery();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name IN ($i,$u,$d)";
        command.Parameters.AddWithValue("$i", RevisionTrigger(document, "insert")); command.Parameters.AddWithValue("$u", RevisionTrigger(document, "update")); command.Parameters.AddWithValue("$d", RevisionTrigger(document, "delete"));
        if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 3)
        {
            foreach (var operation in new[] { "insert", "update", "delete" })
            {
                var name = Quote(RevisionTrigger(document, operation));
                command.CommandText = $"DROP TRIGGER IF EXISTS {name}; CREATE TRIGGER {name} AFTER {operation.ToUpperInvariant()} ON {Quote(document.TableName)} BEGIN UPDATE __wct_log_revision SET revision=revision+1 WHERE document='{document.TableName.Replace("'", "''")}' ; END";
                command.ExecuteNonQuery();
            }
            command.CommandText = "UPDATE __wct_log_revision SET revision=revision+1 WHERE document=$name"; command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public bool IsTimeSeriesCurrent(TimeSeriesResult result)
    {
        lock (gate)
        {
            if (disposed || result.Owner != this || !documents.Contains(result.Document) || !logSeriesCache.Values.Any(c => c.Id == result.Id) || LogSchema(result.Document) != result.Schema) return false;
            using var connection = OpenConnection(true);
            return HasLogRevisionTriggers(connection, result.Document) && LogRevision(connection, result.Document) == result.Revision;
        }
    }

    internal TimeSeriesResult AnalyzeLogTimeSeries(CsvDocument document, TimeSeriesOptions options, CancellationToken token, IProgress<ImportProgress>? progress)
    {
        ArgumentNullException.ThrowIfNull(options); token.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!documents.Contains(document)) throw new InvalidOperationException("この作業領域の表ではありません。");
            ValidateColumn(document, options.TimeColumn);
            if (options.StatusColumn is { } status) ValidateColumn(document, status);
            if (options.BlockColumn is { } block) ValidateColumn(document, block);
            if (!Enum.IsDefined(options.Bucket) || options.ErrorStatusMinimum is < 100 or > 599) throw new ArgumentException("集計間隔またはHTTPエラー閾値が不正です。", nameof(options));
            var time = (options.Time ?? new()) with { OutputKind = TimeOutputKind.Iso8601 }; TimeNormalization.Validate(time);
            var blockValues = (options.BlockValues ?? ["BLOCK"]).Select(v => v?.Trim() ?? "").Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (blockValues.Length > 64 || blockValues.Any(v => v.Length > 1024) || options.BlockColumn is not null && blockValues.Length == 0) throw new ArgumentException("ブロック値は1～64件、各1,024文字以内で指定してください。", nameof(options));
            options = options with { Time = time, BlockValues = Array.AsReadOnly(blockValues) };
            var schema = LogSchema(document);
            using var connection = OpenConnection(); CleanupRecoveredLogCaches(connection); CleanupRecoveredMetricCaches(connection); EnsureLogRevision(connection, document);
            var revision = LogRevision(connection, document); var key = document.TableName + ":" + schema + ":" + revision + ":" + JsonSerializer.Serialize(options);
            if (logSeriesCache.TryGetValue(key, out var cached)) { token.ThrowIfCancellationRequested(); return cached with { CacheHit = true }; }
            var id = Guid.NewGuid().ToString("N"); var rowsTable = "__log_rows_" + id; var seriesTable = "__log_series_" + id;
            try
            {
                using var transaction = connection.BeginTransaction(); using var cancellation = ObserveCancellation(connection, token);
                revision = LogRevision(connection, document, transaction); key = document.TableName + ":" + schema + ":" + revision + ":" + JsonSerializer.Serialize(options);
                connection.CreateFunction<string?, long?>("WCT_LOG_TIME", value =>
                {
                    if (!TimeNormalization.TryParse(value, time, out var instant)) return null;
                    var milliseconds = instant.ToUnixTimeMilliseconds(); return checked((long)decimal.Floor(milliseconds / (1000m * (int)options.Bucket)) * (int)options.Bucket);
                }, true);
                connection.CreateFunction<string?, long?>("WCT_LOG_STATUS", value => int.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var code) && code is >= 100 and <= 599 ? code : null, true);
                connection.CreateFunction<string?, bool>("WCT_LOG_BLOCK", value => blockValues.Contains(value?.Trim() ?? "", StringComparer.OrdinalIgnoreCase), true);
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = $"CREATE TABLE {Quote(rowsTable)}(bucket INTEGER,status INTEGER,blocked INTEGER NOT NULL); CREATE TABLE {Quote(seriesTable)}(bucket INTEGER PRIMARY KEY,requests INTEGER NOT NULL,status_rows INTEGER NOT NULL,errors INTEGER NOT NULL,blocked INTEGER NOT NULL)";
                ExecuteCommand(command, token); progress?.Report(new(0, 0, document.RowCount));
                var statusSql = options.StatusColumn is { } sc ? $"WCT_LOG_STATUS(substr({Quote(document.Columns[sc].Name)},1,64))" : "NULL";
                var blockSql = options.BlockColumn is { } bc ? $"WCT_LOG_BLOCK(substr({Quote(document.Columns[bc].Name)},1,1025))" : "0";
                command.CommandText = $"INSERT INTO {Quote(rowsTable)} SELECT WCT_LOG_TIME(substr({Quote(document.Columns[options.TimeColumn].Name)},1,1025)),{statusSql},{blockSql} FROM {Quote(document.TableName)}";
                ExecuteCommand(command, token);
                command.CommandText = $"INSERT INTO {Quote(seriesTable)} SELECT bucket,COUNT(*),COUNT(status),SUM(CASE WHEN status >= $minimum THEN 1 ELSE 0 END),SUM(blocked) FROM {Quote(rowsTable)} WHERE bucket IS NOT NULL GROUP BY bucket";
                command.Parameters.AddWithValue("$minimum", options.ErrorStatusMinimum); ExecuteCommand(command, token);
                command.CommandText = $"SELECT COUNT(*),COUNT(bucket),SUM(CASE WHEN bucket IS NOT NULL AND status IS NOT NULL THEN 1 ELSE 0 END),SUM(CASE WHEN bucket IS NOT NULL AND status >= $minimum THEN 1 ELSE 0 END),SUM(CASE WHEN bucket IS NOT NULL THEN blocked ELSE 0 END),SUM(CASE WHEN status IS NULL THEN 1 ELSE 0 END) FROM {Quote(rowsTable)}";
                long total, valid, statusRows, errors, blocked, invalidStatus;
                using (var reader = ReadCommand(command, token))
                {
                    ReadNext(reader, token); total = reader.GetInt64(0); valid = reader.GetInt64(1); statusRows = reader.IsDBNull(2) ? 0 : reader.GetInt64(2); errors = reader.IsDBNull(3) ? 0 : reader.GetInt64(3); blocked = reader.IsDBNull(4) ? 0 : reader.GetInt64(4); invalidStatus = options.StatusColumn is null || reader.IsDBNull(5) ? 0 : reader.GetInt64(5);
                }
                command.CommandText = $"SELECT COUNT(*) FROM {Quote(seriesTable)}"; var buckets = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
                command.CommandText = $"DROP TABLE {Quote(rowsTable)}"; ExecuteCommand(command, token);
                var evicted = PlanAnalysisCacheEviction(connection, transaction, token);
                progress?.Report(new(total, total, total)); token.ThrowIfCancellationRequested(); transaction.Commit();
                var result = new TimeSeriesResult(id, document, options, total, valid, total - valid, invalidStatus, buckets, errors, statusRows, blocked, options.BlockColumn is null ? 0 : valid, false) { Revision = revision, Schema = schema, Owner = this };
                logSeriesCache[key] = result;
                CompleteAnalysisCacheInsertion(false, key, id, evicted);
                return result;
            }
            catch (SqliteException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        }
    }

    private void ValidateLogResult(TimeSeriesResult result)
    {
        if (!IsTimeSeriesCurrent(result)) throw new InvalidOperationException("元データが変更されたか、集計結果が期限切れです。もう一度集計してください。");
    }
    internal IReadOnlyList<TimeSeriesBucket> ReadLogTimeSeriesPage(TimeSeriesResult result, long offset, int count, CancellationToken token)
    {
        if (offset < 0 || count is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(count), "集計結果は一度に1～10,000件取得できます。");
        lock (gate)
        {
            ValidateLogResult(result); using var connection = OpenConnection(true); using var cancellation = ObserveCancellation(connection, token);
            using var command = connection.CreateCommand(); command.CommandText = $"SELECT bucket,requests,status_rows,errors,blocked FROM {Quote("__log_series_" + result.Id)} ORDER BY bucket LIMIT $count OFFSET $offset";
            command.Parameters.AddWithValue("$count", count); command.Parameters.AddWithValue("$offset", offset); var rows = new List<TimeSeriesBucket>(count);
            using var reader = ReadCommand(command, token);
            while (ReadNext(reader, token))
            {
                var seconds = reader.GetInt64(0); var requests = reader.GetInt64(1); var statuses = reader.GetInt64(2); var errors = reader.GetInt64(3); var blocked = reader.GetInt64(4);
                rows.Add(new(seconds, TimeNormalization.Format(DateTimeOffset.FromUnixTimeSeconds(seconds), result.Options.Time!.OutputTimeZone), requests, statuses, errors, blocked,
                    result.Options.StatusColumn is null || statuses == 0 ? null : (double)errors / statuses, result.Options.BlockColumn is null || requests == 0 ? null : (double)blocked / requests));
            }
            return rows;
        }
    }
    internal void ExportLogTimeSeriesCsv(TimeSeriesResult result, string path, CancellationToken token)
    {
        lock (gate)
        {
            ValidateLogResult(result); ValidateExportDestination(path); using var output = new AtomicFile(Path.GetFullPath(path));
            using (var connection = OpenConnection(true))
            using (var cancellation = ObserveCancellation(connection, token))
            using (var command = connection.CreateCommand())
            using (var compressed = CompressedText.OpenWrite(output.Stream, path))
            using (var writer = new StreamWriter(compressed, new UTF8Encoding(false), 64 * 1024, true))
            {
                CsvCodec.WriteRecord(writer, ["time", "requests", "valid_status_rows", "errors", "error_rate", "blocked", "block_rate"], ',');
                command.CommandText = $"SELECT bucket,requests,status_rows,errors,blocked FROM {Quote("__log_series_" + result.Id)} ORDER BY bucket";
                using var reader = ReadCommand(command, token);
                while (ReadNext(reader, token))
                {
                    var requests = reader.GetInt64(1); var statuses = reader.GetInt64(2); var errors = reader.GetInt64(3); var blocked = reader.GetInt64(4);
                    var errorRate = result.Options.StatusColumn is null || statuses == 0 ? "" : ((double)errors / statuses).ToString("R", CultureInfo.InvariantCulture);
                    var blockRate = result.Options.BlockColumn is null || requests == 0 ? "" : ((double)blocked / requests).ToString("R", CultureInfo.InvariantCulture);
                    CsvCodec.WriteRecord(writer, [TimeNormalization.Format(DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)), result.Options.Time!.OutputTimeZone), requests.ToString(CultureInfo.InvariantCulture), statuses.ToString(CultureInfo.InvariantCulture), errors.ToString(CultureInfo.InvariantCulture), errorRate, blocked.ToString(CultureInfo.InvariantCulture), blockRate], ',');
                }
                token.ThrowIfCancellationRequested(); writer.Flush();
            }
            output.Commit(token);
        }
    }
}
