using System.Globalization;
using Microsoft.Data.Sqlite;

namespace WinCSVTool.Core;

public sealed partial class Workspace
{
    private static string BuildMetricWhere(CsvDocument document, MetricRangeOptions range, SqliteCommand command)
    {
        var view = new ViewSpec();
        if (range.Filter is { } first) view.Filters.Add(first);
        if (range.Filters is { } filters) view.Filters.AddRange(filters);
        var where = BuildWhere(document, view, command);
        if (range.NumericFilters is not { Count: > 0 } numericFilters) return where;
        var expressions = new List<string>(numericFilters.Count);
        for (var i = 0; i < numericFilters.Count; i++)
        {
            var filter = numericFilters[i];
            var column = Quote(document.Columns[filter.Column].Name);
            var parameter = "$mn" + i;
            command.Parameters.AddWithValue(parameter, filter.Value);
            var op = filter.Comparison switch
            {
                MetricComparison.GreaterThan => ">", MetricComparison.GreaterOrEqual => ">=",
                MetricComparison.LessThan => "<", MetricComparison.LessOrEqual => "<=",
                MetricComparison.Equal => "=", MetricComparison.NotEqual => "<>",
                _ => throw new ArgumentOutOfRangeException(nameof(range))
            };
            expressions.Add($"WCT_NUMBER({column}) {op} WCT_NUMBER({parameter})");
        }
        return where + (where.Length == 0 ? " WHERE " : " AND ") + string.Join(" AND ", expressions);
    }

    private MetricAxisSummary BuildMetricDistribution(SqliteConnection connection, SqliteTransaction transaction, MetricAxisOptions axis, int axisIndex,
        string rows, string points, List<MetricSeriesSummary> series, CancellationToken token, IProgress<ImportProgress>? progress)
    {
        // Counts stay on disk even when the selected column has millions of distinct values.
        using var create = connection.CreateCommand(); create.Transaction = transaction;
        create.CommandText = $"CREATE TABLE {Quote(rows)}(is_blank INTEGER NOT NULL,label TEXT NOT NULL,n INTEGER NOT NULL,PRIMARY KEY(is_blank,label)) WITHOUT ROWID";
        ExecuteCommand(create, token);
        using var select = connection.CreateCommand(); select.Transaction = transaction;
        var range = axis.Range!; var start = range.StartRow ?? 1;
        select.Parameters.AddWithValue("$skip", start - 1); select.Parameters.AddWithValue("$take", range.EndRow is { } end ? end - start + 1 : -1);
        var source = $"(SELECT * FROM {Quote(axis.Document.TableName)} ORDER BY __id LIMIT $take OFFSET $skip)";
        select.CommandText = $"SELECT COUNT(*) FROM {source}";
        var rangeRows = Convert.ToInt64(select.ExecuteScalar(), CultureInfo.InvariantCulture);
        var where = BuildMetricWhere(axis.Document, range, select);
        var timeSql = axis.TimeColumn >= 0 ? $"substr({Quote(axis.Document.Columns[axis.TimeColumn].Name)},1,1025)" : "''";
        var valueSql = Quote(axis.Document.Columns[axis.ValueColumn!.Value].Name);
        var tooLong = $"length(CAST({valueSql} AS BLOB))>262144";
        // Text substr() stops at embedded NUL. Bound bytes before reading, then enforce the character
        // limit below, so accepted grouping values (including control characters) remain exact.
        select.CommandText = $"SELECT {timeSql},CASE WHEN {tooLong} THEN NULL ELSE {valueSql} END,{tooLong} FROM {source}{where} ORDER BY __id";
        using var increment = connection.CreateCommand(); increment.Transaction = transaction;
        increment.CommandText = $"INSERT INTO {Quote(rows)} VALUES($blank,$label,1) ON CONFLICT(is_blank,label) DO UPDATE SET n=n+1";
        increment.Parameters.Add("$blank", SqliteType.Integer); increment.Parameters.Add("$label", SqliteType.Text); increment.Prepare();
        long filtered = 0, invalidTime = 0, outsideTime = 0, invalidValue = 0, outsideNumeric = 0, blankRows = 0, eligible = 0;
        var numericRequired = range.MinimumValue is not null || range.MaximumValue is not null;
        progress?.Report(new(0, 0, rangeRows));
        using (var reader = ReadCommand(select, token)) while (ReadNext(reader, token))
        {
            filtered++;
            if ((filtered & 2047) == 0) progress?.Report(new(filtered, filtered, rangeRows));
            if (axis.TimeColumn >= 0)
            {
                if (!TimeNormalization.TryParse(reader.IsDBNull(0) ? "" : reader.GetString(0), axis.Time!, out var instant)) { invalidTime++; continue; }
                if (range.StartTime is { } from && instant < from || range.EndTime is { } to && instant >= to) { outsideTime++; continue; }
            }
            var value = reader.IsDBNull(1) ? "" : reader.GetString(1); double number = 0;
            if (!reader.IsDBNull(2) && reader.GetInt32(2) != 0 || value.Length > 65536 || numericRequired && (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) || !double.IsFinite(number))) { invalidValue++; continue; }
            if (range.MinimumValue is { } minimum && number < minimum || range.MaximumValue is { } maximum && number > maximum) { outsideNumeric++; continue; }
            var blank = string.IsNullOrWhiteSpace(value);
            if (blank) { blankRows++; if (!axis.IncludeBlank) continue; }
            increment.Parameters[0].Value = blank ? 1 : 0; increment.Parameters[1].Value = blank ? "" : value;
            ExecuteCommand(increment, token); eligible++;
        }
        token.ThrowIfCancellationRequested();
        using var top = connection.CreateCommand(); top.Transaction = transaction;
        top.CommandText = $"SELECT is_blank,label,n FROM {Quote(rows)} ORDER BY n DESC,is_blank,label COLLATE BINARY LIMIT $top";
        top.Parameters.AddWithValue("$top", axis.TopCount);
        var selected = new List<(bool Blank, string Label, long Count)>();
        using (var reader = ReadCommand(top, token)) while (ReadNext(reader, token)) selected.Add((reader.GetInt32(0) != 0, reader.GetString(1), reader.GetInt64(2)));
        var usedLabels = new HashSet<string>(selected.Where(g => !g.Blank).Select(g => g.Label), StringComparer.Ordinal);
        string SyntheticLabel(string name)
        {
            using var exists = connection.CreateCommand(); exists.Transaction = transaction;
            exists.CommandText = $"SELECT EXISTS(SELECT 1 FROM {Quote(rows)} WHERE is_blank=0 AND label=$label)";
            exists.Parameters.Add("$label", SqliteType.Text);
            for (var suffix = 0; ; suffix++)
            {
                token.ThrowIfCancellationRequested();
                var label = suffix == 0 ? name : name + (suffix == 1 ? " (集計)" : " (集計 " + suffix.ToString(CultureInfo.InvariantCulture) + ")");
                exists.Parameters[0].Value = label;
                if (!usedLabels.Contains(label) && Convert.ToInt64(exists.ExecuteScalar(), CultureInfo.InvariantCulture) == 0) { usedLabels.Add(label); return label; }
            }
        }
        using var insert = connection.CreateCommand(); insert.Transaction = transaction;
        insert.CommandText = $"INSERT INTO {Quote(points)}(axis,label,ticks,rowkey,value,n,matched) VALUES($axis,$label,0,0,$value,$n,$matched)";
        insert.Parameters.AddWithValue("$axis", axisIndex); insert.Parameters.Add("$label", SqliteType.Text); insert.Parameters.Add("$value", SqliteType.Real);
        insert.Parameters.AddWithValue("$n", eligible); insert.Parameters.Add("$matched", SqliteType.Integer); insert.Prepare();
        void Add(string label, long count)
        {
            insert.Parameters[1].Value = label; insert.Parameters[2].Value = axis.Measure == MetricMeasure.Rate ? (double)count / eligible : count;
            insert.Parameters[4].Value = count; ExecuteCommand(insert, token);
            // A category total is not a time series; sigma and outlier fields intentionally remain absent.
            series.Add(new(axisIndex, label, 1, 1, 0, 0, null, null, null, null));
        }
        long included = 0;
        foreach (var group in selected) { Add(group.Blank ? SyntheticLabel("(空欄)") : group.Label, group.Count); included += group.Count; }
        if (axis.ShowOther && eligible > included) Add(SyntheticLabel(axis.OtherLabel), eligible - included);
        return new(axisIndex, axis.Document.RowCount, rangeRows, filtered, invalidTime, outsideTime, invalidValue, outsideNumeric, 0, eligible, blankRows);
    }

    private MetricAxisSummary BuildMetricRows(SqliteConnection connection, SqliteTransaction transaction, MetricAxisOptions axis, int axisIndex,
        IReadOnlyList<string> labels, string rows, CancellationToken token, IProgress<ImportProgress>? progress)
    {
        using var create = connection.CreateCommand(); create.Transaction = transaction;
        create.CommandText = $"CREATE TABLE {Quote(rows)}(rid INTEGER NOT NULL,ticks INTEGER NOT NULL,bucket INTEGER NOT NULL,value REAL,mask INTEGER NOT NULL,matched INTEGER NOT NULL)";
        ExecuteCommand(create, token);
        using var select = connection.CreateCommand(); select.Transaction = transaction;
        var range = axis.Range!; var start = range.StartRow ?? 1;
        select.Parameters.AddWithValue("$skip", start - 1); select.Parameters.AddWithValue("$take", range.EndRow is { } end ? end - start + 1 : -1);
        var source = $"(SELECT * FROM {Quote(axis.Document.TableName)} ORDER BY __id LIMIT $take OFFSET $skip)";
        select.CommandText = $"SELECT COUNT(*) FROM {source}";
        var rangeRows = Convert.ToInt64(select.ExecuteScalar(), CultureInfo.InvariantCulture);
        var where = BuildMetricWhere(axis.Document, range, select);
        var limit = axis.Mode == MetricMode.Category ? 65536 : 1024;
        var valueSql = axis.ValueColumn is { } column ? $"substr({Quote(axis.Document.Columns[column].Name)},1,{limit + 1})" : "''";
        select.CommandText = $"SELECT __id,substr({Quote(axis.Document.Columns[axis.TimeColumn].Name)},1,1025),{valueSql} FROM {source}{where} ORDER BY __id";
        using var insert = connection.CreateCommand(); insert.Transaction = transaction;
        insert.CommandText = $"INSERT INTO {Quote(rows)} VALUES($id,$ticks,$bucket,$value,$mask,$matched)";
        foreach (var name in new[] { "$id", "$ticks", "$bucket", "$value", "$mask", "$matched" }) insert.Parameters.Add(name, name == "$value" ? SqliteType.Real : SqliteType.Integer);
        insert.Prepare();
        long filtered = 0, invalidTime = 0, outsideTime = 0, invalidValue = 0, outsideNumeric = 0, unmapped = 0, eligible = 0;
        var mappings = axis.Mappings!; var otherIndex = labels.IndexOfOrdinal(axis.OtherLabel);
        var numericRequired = axis.Mode == MetricMode.Threshold || axis.Mode == MetricMode.Raw && axis.ValueColumn is not null || range.MinimumValue is not null || range.MaximumValue is not null;
        progress?.Report(new(0, 0, rangeRows));
        using (var reader = ReadCommand(select, token)) while (ReadNext(reader, token))
        {
            filtered++;
            if ((filtered & 2047) == 0) progress?.Report(new(filtered, filtered, rangeRows));
            var textTime = reader.IsDBNull(1) ? "" : reader.GetString(1);
            if (!TimeNormalization.TryParse(textTime, axis.Time!, out var instant)) { invalidTime++; continue; }
            if (range.StartTime is { } from && instant < from || range.EndTime is { } to && instant >= to) { outsideTime++; continue; }
            var textValue = reader.IsDBNull(2) ? "" : reader.GetString(2); double number = 0;
            if (textValue.Length > limit || numericRequired && (!double.TryParse(textValue.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) || !double.IsFinite(number))) { invalidValue++; continue; }
            if (range.MinimumValue is { } minimum && number < minimum || range.MaximumValue is { } maximum && number > maximum) { outsideNumeric++; continue; }
            var mask = 1;
            if (axis.Mode == MetricMode.Category)
            {
                mask = 0;
                var tokens = axis.CategoryMatch == MetricCategoryMatch.ContainsToken ? textValue.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : [textValue.Trim()];
                foreach (var mapping in mappings)
                    if (tokens.Contains(mapping.Value, StringComparer.OrdinalIgnoreCase)) mask |= 1 << labels.IndexOfOrdinal(mapping.Label);
                if (mask == 0) { unmapped++; if (axis.Unmapped == MetricUnmappedPolicy.Other) mask = 1 << otherIndex; }
            }
            var matched = axis.Mode == MetricMode.Threshold && CompareMetric(number, axis.Threshold!) ? 1 : 0;
            var ticks = instant.UtcTicks; var bucketSeconds = (long)decimal.Floor((ticks - DateTimeOffset.UnixEpoch.UtcTicks) / ((decimal)TimeSpan.TicksPerSecond * (int)axis.Bucket)) * (int)axis.Bucket;
            // A floored bucket at the beginning of year 1 may predate DateTimeOffset's domain.
            var bucketTicks = Math.Max(DateTimeOffset.MinValue.UtcTicks, checked(DateTimeOffset.UnixEpoch.UtcTicks + bucketSeconds * TimeSpan.TicksPerSecond));
            insert.Parameters[0].Value = reader.GetInt64(0); insert.Parameters[1].Value = ticks; insert.Parameters[2].Value = bucketTicks;
            insert.Parameters[3].Value = numericRequired ? number : DBNull.Value; insert.Parameters[4].Value = mask; insert.Parameters[5].Value = matched;
            ExecuteCommand(insert, token); eligible++;
        }
        token.ThrowIfCancellationRequested();
        return new(axisIndex, axis.Document.RowCount, rangeRows, filtered, invalidTime, outsideTime, invalidValue, outsideNumeric, unmapped, eligible);
    }

    private static bool CompareMetric(double value, MetricThresholdOptions threshold) => threshold.Comparison switch
    {
        MetricComparison.GreaterThan => value > threshold.Value, MetricComparison.GreaterOrEqual => value >= threshold.Value,
        MetricComparison.LessThan => value < threshold.Value, MetricComparison.LessOrEqual => value <= threshold.Value,
        MetricComparison.Equal => value == threshold.Value, MetricComparison.NotEqual => value != threshold.Value, _ => false
    };

    private void BuildMetricPoints(SqliteConnection connection, SqliteTransaction transaction, MetricAxisOptions axis, int axisIndex,
        IReadOnlyList<string> labels, string rows, string points, CancellationToken token)
    {
        using var select = connection.CreateCommand(); select.Transaction = transaction;
        select.CommandText = $"SELECT rid,ticks,bucket,value,mask,matched FROM {Quote(rows)} ORDER BY {(axis.Mode == MetricMode.Raw && axis.Aggregate == MetricAggregate.Samples ? "ticks,rid" : "bucket,rid")}";
        using var insert = connection.CreateCommand(); insert.Transaction = transaction;
        insert.CommandText = $"INSERT INTO {Quote(points)}(axis,label,ticks,rowkey,value,n,matched) VALUES($axis,$label,$ticks,$key,$value,$n,$matched)";
        insert.Parameters.AddWithValue("$axis", axisIndex); insert.Parameters.Add("$label", SqliteType.Text); insert.Parameters.Add("$ticks", SqliteType.Integer);
        insert.Parameters.Add("$key", SqliteType.Integer); insert.Parameters.Add("$value", SqliteType.Real); insert.Parameters.Add("$n", SqliteType.Integer); insert.Parameters.Add("$matched", SqliteType.Integer); insert.Prepare();
        void Add(string label, long ticks, long rowKey, double? value, long count, long matched)
        {
            insert.Parameters[1].Value = label; insert.Parameters[2].Value = ticks; insert.Parameters[3].Value = rowKey;
            insert.Parameters[4].Value = value is { } finite && double.IsFinite(finite) ? finite : DBNull.Value;
            insert.Parameters[5].Value = count; insert.Parameters[6].Value = matched; ExecuteCommand(insert, token);
        }
        long? bucket = null; long count = 0, matchedCount = 0; var labelCounts = new long[labels.Count]; var statistics = new MetricStatistics();
        void FinishBucket()
        {
            if (bucket is null) return;
            if (axis.Mode == MetricMode.Category)
            {
                for (var i = 0; i < labels.Count; i++) Add(labels[i], bucket.Value, 0, axis.Measure == MetricMeasure.Rate ? (double)labelCounts[i] / count : labelCounts[i], count, labelCounts[i]);
            }
            else if (axis.Mode == MetricMode.Threshold) Add(labels[0], bucket.Value, 0, axis.Measure == MetricMeasure.Rate ? (double)matchedCount / count : matchedCount, count, matchedCount);
            else
            {
                var value = axis.Aggregate switch { MetricAggregate.Mean => statistics.Mean, MetricAggregate.Sum => statistics.Sum, MetricAggregate.Minimum => statistics.Minimum, MetricAggregate.Maximum => statistics.Maximum, MetricAggregate.Count => (double)count, _ => null };
                Add(labels[0], bucket.Value, 0, value, count, 0);
            }
        }
        using var reader = ReadCommand(select, token);
        while (ReadNext(reader, token))
        {
            if (axis.Mode == MetricMode.Raw && axis.Aggregate == MetricAggregate.Samples) { Add(labels[0], reader.GetInt64(1), reader.GetInt64(0), reader.GetDouble(3), 1, 0); continue; }
            var current = reader.GetInt64(2);
            if (bucket != current) { FinishBucket(); bucket = current; count = matchedCount = 0; Array.Clear(labelCounts); statistics = new(); }
            count++; if (!reader.IsDBNull(3)) statistics.Add(reader.GetDouble(3)); matchedCount += reader.GetInt64(5);
            var mask = reader.GetInt32(4); for (var i = 0; i < labels.Count; i++) if ((mask & 1 << i) != 0) labelCounts[i]++;
        }
        FinishBucket();
    }

    private IReadOnlyList<MetricSeriesSummary> FinalizeMetricSeries(SqliteConnection connection, SqliteTransaction transaction,
        MetricAxisOptions axis, int axisIndex, IReadOnlyList<string> labels, string points, CancellationToken token)
    {
        var result = new List<MetricSeriesSummary>(labels.Count);
        foreach (var label in labels)
        {
            using var select = connection.CreateCommand(); select.Transaction = transaction;
            select.CommandText = $"SELECT value FROM {Quote(points)} WHERE axis=$axis AND label=$label ORDER BY pid";
            select.Parameters.AddWithValue("$axis", axisIndex); select.Parameters.AddWithValue("$label", label);
            var statistics = new MetricStatistics(); long total = 0;
            using (var reader = ReadCommand(select, token)) while (ReadNext(reader, token)) { total++; if (!reader.IsDBNull(0)) statistics.Add(reader.GetDouble(0)); }
            var band = statistics.Band(axis.Sigma); long outliers = 0;
            if (axis.Outliers != MetricOutlierPolicy.None && band.Low is { } low && band.High is { } high)
            {
                using var update = connection.CreateCommand(); update.Transaction = transaction;
                update.CommandText = $"UPDATE {Quote(points)} SET outlier=1 WHERE axis=$axis AND label=$label AND (value<$low OR value>$high)";
                update.Parameters.AddWithValue("$axis", axisIndex); update.Parameters.AddWithValue("$label", label); update.Parameters.AddWithValue("$low", low); update.Parameters.AddWithValue("$high", high);
                ExecuteCommand(update, token);
                using (var changed = connection.CreateCommand()) { changed.Transaction = transaction; changed.CommandText = "SELECT changes()"; outliers = Convert.ToInt64(changed.ExecuteScalar(), CultureInfo.InvariantCulture); }
                if (axis.Outliers == MetricOutlierPolicy.Exclude) { update.CommandText = $"DELETE FROM {Quote(points)} WHERE axis=$axis AND label=$label AND outlier=1"; ExecuteCommand(update, token); }
            }
            result.Add(new(axisIndex, label, total, statistics.Count, outliers, axis.Outliers == MetricOutlierPolicy.Exclude ? outliers : 0, statistics.Mean, statistics.StandardDeviation, band.Low, band.High));
        }
        return result;
    }
}

internal static class MetricLabelExtensions
{
    public static int IndexOfOrdinal(this IReadOnlyList<string> labels, string value)
    {
        for (var i = 0; i < labels.Count; i++) if (labels[i] == value) return i;
        return -1;
    }
}
