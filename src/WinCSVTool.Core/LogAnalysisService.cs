namespace WinCSVTool.Core;

public enum TimeBucketUnit { Second = 1, Minute = 60, Hour = 3600 }
public sealed record TimeSeriesOptions(int TimeColumn, TimeBucketUnit Bucket = TimeBucketUnit.Minute, TimeParseOptions? Time = null,
    int? StatusColumn = null, int ErrorStatusMinimum = 400, int? BlockColumn = null, IReadOnlyList<string>? BlockValues = null);
public sealed record TimeSeriesBucket(long UnixSeconds, string Time, long Requests, long ValidStatusRows, long Errors, long Blocked, double? ErrorRate, double? BlockRate);
public sealed record TimeSeriesResult(string Id, CsvDocument Document, TimeSeriesOptions Options, long TotalRows, long ValidTimeRows,
    long InvalidTimeRows, long InvalidStatusRows, long BucketCount, long ErrorCount, long ErrorDenominator, long BlockedCount, long BlockDenominator, bool CacheHit)
{
    internal long Revision { get; init; }
    internal string Schema { get; init; } = "";
    internal Workspace? Owner { get; init; }
}
public sealed record LogTransformSummary(long ProcessedRows, long InvalidRows, long MatchedRows);

public sealed class LogAnalysisService(Workspace workspace)
{
    public TimeSeriesResult AnalyzeTimeSeries(CsvDocument document, TimeSeriesOptions options, CancellationToken token = default, IProgress<ImportProgress>? progress = null)
        => workspace.AnalyzeLogTimeSeries(document, options, token, progress);
    public bool IsTimeSeriesCurrent(TimeSeriesResult result) => workspace.IsTimeSeriesCurrent(result);
    public IReadOnlyList<TimeSeriesBucket> ReadTimeSeriesPage(TimeSeriesResult result, long offset, int count = 1000, CancellationToken token = default)
        => workspace.ReadLogTimeSeriesPage(result, offset, count, token);
    public void ExportTimeSeriesCsv(TimeSeriesResult result, string path, CancellationToken token = default) => workspace.ExportLogTimeSeriesCsv(result, path, token);
}
