namespace WinCSVTool.Core;

public enum DataKind { Auto, Text, Number, Timestamp, Boolean }
public sealed class CsvColumn(string name)
{
    public string Name { get; set; } = name;
    public string HeaderName { get; set; } = name;
    public DataKind Kind { get; set; } = DataKind.Auto;
}
public sealed class CsvDocument
{
    public required string TableName { get; init; }
    public required string DisplayName { get; set; }
    public string? SourcePath { get; set; }
    public string? ImportSourcePath { get; set; }
    public string? WorkbookId { get; set; }
    public string? WorksheetName { get; set; }
    public string? WorkbookSourcePath { get; set; }
    public string EncodingName { get; set; } = "utf-8";
    public bool HasHeader { get; set; } = true;
    public char Delimiter { get; set; } = ',';
    public long RowCount { get; set; }
    public bool IsDirty { get; set; }
    public List<CsvColumn> Columns { get; } = [];
}
public sealed record CsvOptions(string EncodingName = "auto", bool HasHeader = true, char Delimiter = ',', bool WriteBom = false, bool EscapeSpreadsheetFormulas = false);
public sealed record ImportProgress(long Rows, long BytesRead, long TotalBytes);
public sealed record CsvRow(long Id, string[] Values);
public enum FilterOperator { Contains, Equals, NotEquals, StartsWith, EndsWith, GreaterThan, LessThan, Empty, NotEmpty, Regex }
public sealed record FilterRule(int Column, FilterOperator Operator, string Value);
public sealed record SortRule(int Column, bool Descending = false);
public sealed class ViewSpec
{
    public List<FilterRule> Filters { get; } = [];
    public List<SortRule> Sorts { get; } = [];
    public bool IsEmpty => Filters.Count == 0 && Sorts.Count == 0;
}
public sealed record FindMatch(long RowId, int Column, string Value, long Offset);
