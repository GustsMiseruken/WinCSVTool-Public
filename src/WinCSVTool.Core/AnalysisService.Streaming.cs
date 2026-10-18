using Microsoft.Data.Sqlite;

namespace WinCSVTool.Core;

public sealed partial class AnalysisService
{
    /// <summary>Streams all query rows to a caller-owned writer, including CLI standard output.</summary>
    public long WriteQuery(string sql, TextWriter writer, char delimiter = ',', bool includeHeader = true, CancellationToken token = default, bool escapeSpreadsheetFormulas = false)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ValidateQuery(sql); CsvCodec.ValidateDelimiter(delimiter);
        using var connection = workspace.OpenConnection(readOnly: true);
        PrepareQueryConnection(connection);
        using var cancellation = InterruptOnCancellation(connection, token);
        using var command = connection.CreateCommand(); command.CommandText = sql;
        try
        {
            token.ThrowIfCancellationRequested();
            using var reader = command.ExecuteReader();
            if (includeHeader) CsvCodec.WriteRecord(writer, ReadColumns(reader), delimiter, escapeSpreadsheetFormulas);
            long count = 0;
            while (reader.Read()) { token.ThrowIfCancellationRequested(); CsvCodec.WriteRecord(writer, ReadValues(reader), delimiter, escapeSpreadsheetFormulas); count++; }
            token.ThrowIfCancellationRequested(); writer.Flush(); return count;
        }
        catch (SqliteException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
    }
}
