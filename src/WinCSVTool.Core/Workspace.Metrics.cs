using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace WinCSVTool.Core;

public sealed partial class Workspace
{
    private readonly Dictionary<string, MetricAnalysisResult> metricCache = new(StringComparer.Ordinal);
    private readonly Queue<(bool Metric, string Key, string Id)> analysisCacheOrder = new();
    private bool metricCachesCleaned;

    // Both analysis APIs share four disk snapshots. Eviction is transactional; cancellation retains the previous snapshots.
    private (bool Metric, string Key, string Id)? PlanAnalysisCacheEviction(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        if (metricCache.Count + logSeriesCache.Count < 4) return null;
        var entry = analysisCacheOrder.First(e => e.Metric ? metricCache.ContainsKey(e.Key) : logSeriesCache.ContainsKey(e.Key));
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = $"DROP TABLE IF EXISTS {Quote((entry.Metric ? "__metric_points_" : "__log_series_") + entry.Id)}";
        ExecuteCommand(command, token); return entry;
    }
    private void CompleteAnalysisCacheInsertion(bool metric, string key, string id, (bool Metric, string Key, string Id)? evicted)
    {
        if (evicted is { } entry) { if (entry.Metric) metricCache.Remove(entry.Key); else logSeriesCache.Remove(entry.Key); }
        while (analysisCacheOrder.TryPeek(out var first) && !(first.Metric ? metricCache.ContainsKey(first.Key) : logSeriesCache.ContainsKey(first.Key))) analysisCacheOrder.Dequeue();
        analysisCacheOrder.Enqueue((metric, key, id));
    }
    private void CleanupRecoveredMetricCaches(SqliteConnection connection)
    {
        if (metricCachesCleaned) return;
        using var command = connection.CreateCommand(); command.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
        var names = new List<string>();
        using (var reader = command.ExecuteReader()) while (reader.Read())
        {
            var name = reader.GetString(0);
            if (Regex.IsMatch(name, "^__metric_(points_[0-9a-f]{32}|rows_[0-9a-f]{32}_[01])$", RegexOptions.CultureInvariant)) names.Add(name);
        }
        using var transaction = connection.BeginTransaction(); command.Transaction = transaction;
        foreach (var name in names) { command.CommandText = $"DROP TABLE {Quote(name)}"; command.ExecuteNonQuery(); }
        transaction.Commit(); metricCachesCleaned = true;
    }

    private MetricAxisOptions[] ValidateMetricAxes(IReadOnlyList<MetricAxisOptions> axes)
    {
        ArgumentNullException.ThrowIfNull(axes);
        if (axes.Count is < 1 or > 2) throw new ArgumentException("分析対象は最大2系列（軸A・軸B）です。", nameof(axes));
        var frozen = new MetricAxisOptions[axes.Count];
        for (var i = 0; i < axes.Count; i++)
        {
            var axis = axes[i] ?? throw new ArgumentException("分析対象がありません。", nameof(axes));
            if (!documents.Contains(axis.Document)) throw new InvalidOperationException("この作業領域の表ではありません。");
            if (axis.Mode != MetricMode.Distribution || axis.TimeColumn != -1) ValidateColumn(axis.Document, axis.TimeColumn);
            if (axis.ValueColumn is { } column) ValidateColumn(axis.Document, column);
            if (!Enum.IsDefined(axis.Mode) || !Enum.IsDefined(axis.Aggregate) || !Enum.IsDefined(axis.Bucket) || !Enum.IsDefined(axis.Measure) || !Enum.IsDefined(axis.CategoryMatch) || !Enum.IsDefined(axis.Unmapped) || !Enum.IsDefined(axis.Outliers) || axis.Sigma is < 1 or > 3)
                throw new ArgumentException("集計方法または標準偏差の設定が不正です。", nameof(axes));
            if (axis.ValueColumn is null && !(axis.Mode == MetricMode.Raw && axis.Aggregate == MetricAggregate.Count)) throw new ArgumentException("値の列を選択してください。", nameof(axes));
            if (axis.Name?.Length > 120 || axis.OtherLabel is null) throw new ArgumentException("系列名またはその他のラベルが不正です。", nameof(axes));
            var time = (axis.Time ?? new()) with { OutputKind = TimeOutputKind.Iso8601 }; TimeNormalization.Validate(time);
            var range = axis.Range ?? new();
            if (axis.Mode == MetricMode.Distribution && (axes.Count != 1 || axis.ValueColumn is null || axis.Outliers != MetricOutlierPolicy.None || axis.TopCount is < 1 or > 20))
                throw new ArgumentException("項目別内訳は1軸、項目の列、上位1～20件、外れ値の処理なしで指定してください。", nameof(axes));
            if (axis.Mode == MetricMode.Distribution && axis.TimeColumn == -1 && (range.StartTime is not null || range.EndTime is not null))
                throw new ArgumentException("期間を指定する場合は時刻の列を選択してください。", nameof(axes));
            if (range.StartRow is < 1 || range.EndRow is < 1 || range.StartRow > range.EndRow || range.StartTime >= range.EndTime ||
                range.MinimumValue is { } minimum && !double.IsFinite(minimum) || range.MaximumValue is { } maximum && !double.IsFinite(maximum) || range.MinimumValue > range.MaximumValue)
                throw new ArgumentException("対象範囲の開始・終了値が不正です。", nameof(axes));
            if ((range.MinimumValue is not null || range.MaximumValue is not null) && axis.ValueColumn is null) throw new ArgumentException("数値範囲には値の列が必要です。", nameof(axes));
            var filters = range.Filters?.Select(f => f ?? throw new ArgumentException("絞り込み条件が不正です。", nameof(axes))).ToArray() ?? [];
            var numericFilters = range.NumericFilters?.Select(f => f ?? throw new ArgumentException("数値の絞り込み条件が不正です。", nameof(axes))).ToArray() ?? [];
            if (filters.Length + numericFilters.Length + (range.Filter is null ? 0 : 1) > 8) throw new ArgumentException("絞り込み条件は最大8件です。", nameof(axes));
            foreach (var filter in (range.Filter is { } first ? new[] { first }.Concat(filters) : filters))
            {
                ValidateColumn(axis.Document, filter.Column);
                if (!Enum.IsDefined(filter.Operator) || filter.Value is null || filter.Value.Length > 4096) throw new ArgumentException("絞り込み条件が不正です。", nameof(axes));
            }
            foreach (var filter in numericFilters)
            {
                ValidateColumn(axis.Document, filter.Column);
                if (!Enum.IsDefined(filter.Comparison) || filter.Value is null || filter.Value.Length > 4096 || ParseNumber(filter.Value) is null)
                    throw new ArgumentException("数値の絞り込みには有効な有限数値を入力してください。", nameof(axes));
            }
            range = range with { Filters = Array.AsReadOnly(filters), NumericFilters = Array.AsReadOnly(numericFilters) };
            var threshold = axis.Threshold ?? new();
            if (!Enum.IsDefined(threshold.Comparison) || !double.IsFinite(threshold.Value)) throw new ArgumentException("しきい値が不正です。", nameof(axes));
            var mappings = axis.Mappings?.Select(m => m ?? throw new ArgumentException("分類の対応が不正です。", nameof(axes))).ToArray() ?? [];
            if (mappings.Length > 32 || mappings.Any(m => string.IsNullOrWhiteSpace(m.Value) || string.IsNullOrWhiteSpace(m.Label) || m.Value.Length > 1024 || m.Label.Length > 120) || axis.Mode == MetricMode.Category && mappings.Length == 0)
                throw new ArgumentException("分類は1～32件、値は1,024文字、ラベルは120文字以内で指定してください。", nameof(axes));
            mappings = mappings.Select(m => m with { Value = m.Value.Trim(), Label = m.Label.Trim() }).ToArray();
            if (axis.Mode == MetricMode.Category && mappings.GroupBy(m => m.Value, StringComparer.OrdinalIgnoreCase).Any(g => g.Select(m => m.Label).Distinct(StringComparer.Ordinal).Count() > 1))
                throw new ArgumentException("同じ分類値を異なるラベルへ重複割当できません。", nameof(axes));
            if ((axis.Mode == MetricMode.Category && axis.Unmapped == MetricUnmappedPolicy.Other || axis.Mode == MetricMode.Distribution && axis.ShowOther) && (string.IsNullOrWhiteSpace(axis.OtherLabel) || axis.OtherLabel.Length > 120))
                throw new ArgumentException("その他のラベルは1～120文字です。", nameof(axes));
            var labels = mappings.Select(m => m.Label).Distinct(StringComparer.Ordinal).ToList();
            if (axis.Unmapped == MetricUnmappedPolicy.Other && !labels.Contains(axis.OtherLabel.Trim(), StringComparer.Ordinal)) labels.Add(axis.OtherLabel.Trim());
            if (axis.Mode == MetricMode.Category && labels.Count > 8) throw new ArgumentException("分類ラベルはその他を含め最大8種類です。", nameof(axes));
            frozen[i] = axis with { Time = time, Range = range, Threshold = threshold, Mappings = Array.AsReadOnly(mappings), OtherLabel = axis.OtherLabel.Trim() };
        }
        return frozen;
    }

    private static string MetricCacheKey(IReadOnlyList<MetricAxisOptions> axes, string[] schemas, long[] revisions) => JsonSerializer.Serialize(axes.Select((a, i) => new
    {
        a.Document.TableName, Schema = schemas[i], Revision = revisions[i], a.TimeColumn, a.ValueColumn, a.Mode, a.Aggregate, a.Bucket, a.Time,
        a.Range, a.Threshold, a.Mappings, a.CategoryMatch, a.Unmapped, a.OtherLabel, a.Measure, a.Outliers, a.Sigma, a.Name, a.TopCount, a.IncludeBlank, a.ShowOther
    }));

    internal MetricAnalysisResult AnalyzeMetrics(IReadOnlyList<MetricAxisOptions> requested, CancellationToken token, IProgress<ImportProgress>? progress)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            var axes = ValidateMetricAxes(requested); var schemas = axes.Select(a => LogSchema(a.Document)).ToArray();
            using var connection = OpenConnection(); CleanupRecoveredLogCaches(connection); CleanupRecoveredMetricCaches(connection);
            foreach (var doc in axes.Select(a => a.Document).Distinct()) EnsureLogRevision(connection, doc);
            var revisions = axes.Select(a => LogRevision(connection, a.Document)).ToArray(); var key = MetricCacheKey(axes, schemas, revisions);
            if (metricCache.TryGetValue(key, out var cached)) { token.ThrowIfCancellationRequested(); return cached with { CacheHit = true }; }
            var id = Guid.NewGuid().ToString("N"); var points = "__metric_points_" + id;
            try
            {
                using var transaction = connection.BeginTransaction(); using var cancellation = ObserveCancellation(connection, token);
                revisions = axes.Select(a => LogRevision(connection, a.Document, transaction)).ToArray(); key = MetricCacheKey(axes, schemas, revisions);
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = $"CREATE TABLE {Quote(points)}(pid INTEGER PRIMARY KEY,axis INTEGER NOT NULL,label TEXT NOT NULL,ticks INTEGER NOT NULL,rowkey INTEGER NOT NULL,value REAL,n INTEGER NOT NULL,matched INTEGER NOT NULL,outlier INTEGER NOT NULL DEFAULT 0)";
                ExecuteCommand(command, token);
                var summaries = new List<MetricAxisSummary>(); var series = new List<MetricSeriesSummary>();
                for (var i = 0; i < axes.Length; i++)
                {
                    var rows = "__metric_rows_" + id + "_" + i;
                    if (axes[i].Mode == MetricMode.Distribution)
                    {
                        summaries.Add(BuildMetricDistribution(connection, transaction, axes[i], i, rows, points, series, token, progress));
                        command.CommandText = $"DROP TABLE {Quote(rows)}"; ExecuteCommand(command, token);
                        continue;
                    }
                    var labels = axes[i].Mode == MetricMode.Category ? axes[i].Mappings!.Select(m => m.Label).Distinct(StringComparer.Ordinal).ToList() : [axes[i].Name ?? ("系列" + (i + 1))];
                    if (axes[i].Mode == MetricMode.Category && axes[i].Unmapped == MetricUnmappedPolicy.Other && !labels.Contains(axes[i].OtherLabel, StringComparer.Ordinal)) labels.Add(axes[i].OtherLabel);
                    summaries.Add(BuildMetricRows(connection, transaction, axes[i], i, labels, rows, token, progress));
                    BuildMetricPoints(connection, transaction, axes[i], i, labels, rows, points, token);
                    series.AddRange(FinalizeMetricSeries(connection, transaction, axes[i], i, labels, points, token));
                    command.CommandText = $"DROP TABLE {Quote(rows)}"; ExecuteCommand(command, token);
                }
                command.CommandText = $"CREATE INDEX {Quote(points + "_order")} ON {Quote(points)}(ticks,axis,label,rowkey); SELECT COUNT(*) FROM {Quote(points)}";
                var count = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
                var evicted = PlanAnalysisCacheEviction(connection, transaction, token);
                progress?.Report(new(summaries.Sum(s => s.FilteredRows), summaries.Sum(s => s.FilteredRows), summaries.Sum(s => s.FilteredRows)));
                token.ThrowIfCancellationRequested(); transaction.Commit();
                var result = new MetricAnalysisResult(id, Array.AsReadOnly(axes), count, summaries.AsReadOnly(), series.AsReadOnly(), false)
                { Owner = this, Revisions = Array.AsReadOnly(revisions), Schemas = Array.AsReadOnly(schemas) };
                metricCache[key] = result; CompleteAnalysisCacheInsertion(true, key, id, evicted); return result;
            }
            catch (SqliteException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        }
    }

    public bool IsMetricAnalysisCurrent(MetricAnalysisResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (gate)
        {
            if (disposed || result.Owner != this || !metricCache.Values.Any(c => c.Id == result.Id)) return false;
            using var connection = OpenConnection(true);
            for (var i = 0; i < result.Axes.Count; i++)
            {
                var document = result.Axes[i].Document;
                if (!documents.Contains(document) || LogSchema(document) != result.Schemas[i] || !HasLogRevisionTriggers(connection, document) || LogRevision(connection, document) != result.Revisions[i]) return false;
            }
            return true;
        }
    }
    private void ValidateMetricResult(MetricAnalysisResult result)
    {
        if (!IsMetricAnalysisCurrent(result)) throw new InvalidOperationException("元データが変更されたか、集計結果が期限切れです。もう一度集計してください。");
    }

    internal IReadOnlyList<MetricPoint> ReadMetricPage(MetricAnalysisResult result, long offset, int count, CancellationToken token)
    {
        if (offset < 0 || count is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(count), "結果は一度に1～1,000点取得できます。");
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            ValidateMetricResult(result); using var connection = OpenConnection(true); using var cancellation = ObserveCancellation(connection, token);
            using var command = connection.CreateCommand(); command.CommandText = $"SELECT axis,label,ticks,rowkey,value,n,matched,outlier FROM {Quote("__metric_points_" + result.Id)} ORDER BY ticks,axis,label,rowkey LIMIT $count OFFSET $offset";
            if (result.Axes[0].Mode == MetricMode.Distribution) command.CommandText = $"SELECT axis,label,ticks,rowkey,value,n,matched,outlier FROM {Quote("__metric_points_" + result.Id)} ORDER BY matched DESC,label COLLATE BINARY LIMIT $count OFFSET $offset";
            command.Parameters.AddWithValue("$count", count); command.Parameters.AddWithValue("$offset", offset);
            var rows = new List<MetricPoint>(count); using var reader = ReadCommand(command, token);
            while (ReadNext(reader, token)) rows.Add(ReadMetricPoint(result, reader));
            return rows;
        }
    }
    private static MetricPoint ReadMetricPoint(MetricAnalysisResult result, SqliteDataReader reader)
    {
        var axis = reader.GetInt32(0); var label = reader.GetString(1); var instant = new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero);
        var summary = result.SeriesSummaries.First(s => s.Axis == axis && s.Label == label);
        var distribution = result.Axes[axis].Mode == MetricMode.Distribution;
        return new(axis, label, reader.GetInt64(3), distribution ? 0 : instant.ToUnixTimeMilliseconds(), distribution ? "" : TimeNormalization.Format(instant, result.Axes[axis].Time!.OutputTimeZone), reader.IsDBNull(4) ? null : reader.GetDouble(4), reader.GetInt64(5), reader.GetInt64(6), summary.Mean, summary.StandardDeviation, summary.BandLow, summary.BandHigh, reader.GetInt64(7));
    }
    internal void ExportMetricsCsv(MetricAnalysisResult result, string path, CsvOptions options, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(options);
        CsvCodec.ValidateDelimiter(options.Delimiter);
        var encoding = EncodingDetector.Resolve(options.EncodingName.Equals("auto", StringComparison.OrdinalIgnoreCase) ? "utf-8" : options.EncodingName, options.WriteBom);
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            ValidateMetricResult(result); ValidateExportDestination(path); using var output = new AtomicFile(Path.GetFullPath(path));
            using (var connection = OpenConnection(true))
            using (var cancellation = ObserveCancellation(connection, token))
            using (var command = connection.CreateCommand())
            using (var compressed = CompressedText.OpenWrite(output.Stream, path))
            using (var writer = new StreamWriter(compressed, encoding, 64 * 1024, true))
            {
                if (options.HasHeader) CsvCodec.WriteRecord(writer, ["axis", "label", "time", "value", "count", "matched_count", "population_mean", "population_standard_deviation", "band_low", "band_high", "outlier"], options.Delimiter, options.EscapeSpreadsheetFormulas);
                command.CommandText = $"SELECT axis,label,ticks,rowkey,value,n,matched,outlier FROM {Quote("__metric_points_" + result.Id)} ORDER BY ticks,axis,label,rowkey";
                if (result.Axes[0].Mode == MetricMode.Distribution) command.CommandText = $"SELECT axis,label,ticks,rowkey,value,n,matched,outlier FROM {Quote("__metric_points_" + result.Id)} ORDER BY matched DESC,label COLLATE BINARY";
                using var reader = ReadCommand(command, token);
                while (ReadNext(reader, token))
                {
                    var p = ReadMetricPoint(result, reader);
                    CsvCodec.WriteRecord(writer, [(p.Axis + 1).ToString(CultureInfo.InvariantCulture), p.Label, p.Time, MetricNumber(p.Value), p.Count.ToString(CultureInfo.InvariantCulture), p.MatchedCount.ToString(CultureInfo.InvariantCulture), MetricNumber(p.Mean), MetricNumber(p.StandardDeviation), MetricNumber(p.BandLow), MetricNumber(p.BandHigh), p.OutlierCount.ToString(CultureInfo.InvariantCulture)], options.Delimiter, options.EscapeSpreadsheetFormulas);
                }
                token.ThrowIfCancellationRequested(); writer.Flush();
            }
            output.Commit(token);
        }
    }
    private static string MetricNumber(double? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? "";
}
