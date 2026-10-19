namespace WinCSVTool.Core;

public enum MetricMode { Raw, Threshold, Category, Distribution }
public enum MetricAggregate { Samples, Mean, Sum, Minimum, Maximum, Count }
public enum MetricMeasure { Count, Rate }
public enum MetricComparison { GreaterThan, GreaterOrEqual, LessThan, LessOrEqual, Equal, NotEqual }
public enum MetricCategoryMatch { Equals, ContainsToken }
public enum MetricUnmappedPolicy { Exclude, Other }
public enum MetricOutlierPolicy { None, Mark, Exclude }

/// <summary>Rows are one-based inclusive ordinals in internal row-ID order. Time bounds are [start,end); numeric bounds are inclusive.</summary>
public sealed record MetricRangeOptions(long? StartRow = null, long? EndRow = null,
    DateTimeOffset? StartTime = null, DateTimeOffset? EndTime = null,
    double? MinimumValue = null, double? MaximumValue = null, FilterRule? Filter = null,
    IReadOnlyList<FilterRule>? Filters = null, IReadOnlyList<MetricNumericFilter>? NumericFilters = null);
/// <summary>Explicit finite numeric comparison independent of the source column's data kind. Blank and nonnumeric source values do not match.</summary>
public sealed record MetricNumericFilter(int Column, MetricComparison Comparison, string Value);
public sealed record MetricThresholdOptions(MetricComparison Comparison = MetricComparison.GreaterOrEqual, double Value = 400);
public sealed record MetricCategoryMapping(string Value, string Label);
public sealed record MetricAxisOptions(CsvDocument Document, int TimeColumn, int? ValueColumn = null,
    MetricMode Mode = MetricMode.Raw, MetricAggregate Aggregate = MetricAggregate.Mean,
    TimeBucketUnit Bucket = TimeBucketUnit.Minute, TimeParseOptions? Time = null,
    MetricRangeOptions? Range = null, MetricThresholdOptions? Threshold = null,
    IReadOnlyList<MetricCategoryMapping>? Mappings = null, MetricCategoryMatch CategoryMatch = MetricCategoryMatch.Equals,
    MetricUnmappedPolicy Unmapped = MetricUnmappedPolicy.Exclude, string OtherLabel = "Other",
    MetricMeasure Measure = MetricMeasure.Count, MetricOutlierPolicy Outliers = MetricOutlierPolicy.Mark,
    int Sigma = 2, string? Name = null, int TopCount = 10, bool IncludeBlank = false, bool ShowOther = true);

/// <summary>Rate values are fractions (0..1). Mean/deviation/bands describe pre-exclusion plotted values, not individual observations of aggregated buckets.</summary>
public sealed record MetricPoint(int Axis, string Label, long RowKey, long UnixMilliseconds, string Time,
    double? Value, long Count, long MatchedCount, double? Mean, double? StandardDeviation,
    double? BandLow, double? BandHigh, long OutlierCount);
public sealed record MetricAxisSummary(int Axis, long SourceRows, long RangeRows, long FilteredRows,
    long InvalidTimeRows, long OutsideTimeRows, long InvalidValueRows, long OutsideNumericRows,
    long UnmappedRows, long EligibleRows, long BlankValueRows = 0);
public sealed record MetricSeriesSummary(int Axis, string Label, long PointCount, long FinitePointCount,
    long OutlierCount, long ExcludedPointCount, double? Mean, double? StandardDeviation,
    double? BandLow, double? BandHigh);
public sealed record MetricAnalysisResult(string Id, IReadOnlyList<MetricAxisOptions> Axes, long PointCount,
    IReadOnlyList<MetricAxisSummary> AxisSummaries, IReadOnlyList<MetricSeriesSummary> SeriesSummaries, bool CacheHit)
{
    internal Workspace? Owner { get; init; }
    internal IReadOnlyList<long> Revisions { get; init; } = [];
    internal IReadOnlyList<string> Schemas { get; init; } = [];
}

public sealed class MetricAnalysisService(Workspace workspace)
{
    public MetricAnalysisResult Analyze(IReadOnlyList<MetricAxisOptions> axes, CancellationToken token = default, IProgress<ImportProgress>? progress = null)
        => workspace.AnalyzeMetrics(axes, token, progress);
    public bool IsCurrent(MetricAnalysisResult result) => workspace.IsMetricAnalysisCurrent(result);
    /// <summary>Returns at most 1,000 combined points, ordered by precise instant, axis, label, then row key. Samples retain repeated timestamps. Distribution results are ordered by count descending and label.</summary>
    public IReadOnlyList<MetricPoint> ReadPage(MetricAnalysisResult result, long offset, int count = 1000, CancellationToken token = default)
        => workspace.ReadMetricPage(result, offset, count, token);
    public void ExportCsv(MetricAnalysisResult result, string path, CancellationToken token = default)
        => ExportCsv(result, path, new CsvOptions("utf-8"), token);
    /// <summary>Exports public result columns. Spreadsheet-formula protection is an explicit value-changing option, as with document CSV exports.</summary>
    public void ExportCsv(MetricAnalysisResult result, string path, CsvOptions options, CancellationToken token)
        => workspace.ExportMetricsCsv(result, path, options, token);
}
