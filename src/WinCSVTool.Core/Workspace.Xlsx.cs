using Microsoft.Data.Sqlite;

namespace WinCSVTool.Core;

public sealed partial class Workspace
{
    internal IReadOnlyList<CsvDocument> ImportXlsx(string path, IReadOnlyList<XlsxSheetSource> sheets, bool hasHeader,
        Action<SqliteConnection, SqliteTransaction> prepareStrings, Action<SqliteConnection, SqliteTransaction> finishStrings,
        CancellationToken token)
    {
        lock (gate)
        {
            EnsureDocumentCapacity(sheets.Count);
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            prepareStrings(connection, transaction);
            var imported = new List<CsvDocument>();
            var newMaps = new List<ViewMap>();
            var workbookId = Guid.NewGuid().ToString("N");
            foreach (var sheet in sheets)
            {
                token.ThrowIfCancellationRequested();
                using var rows = sheet.ReadRows().GetEnumerator();
                var first = rows.MoveNext() ? rows.Current : null;
                var width = Math.Max(1, first?.Values.Length ?? 0);
                var headers = hasHeader && first is not null ? first.Values : Enumerable.Range(1, width).Select(i => $"Column{i}").ToArray();
                var document = NewDocument(sheet.Name, headers);
                document.SourcePath = path;
                document.WorkbookSourcePath = path;
                document.WorkbookId = workbookId;
                document.WorksheetName = sheet.Name;
                document.EncodingName = "utf-8";
                document.HasHeader = hasHeader;
                CheckXlsxColumnLimit(document.Columns.Count);
                CreateTable(connection, transaction, document);
                SqliteCommand? insert = null;
                try
                {
                    void AddRow(XlsxRow row)
                    {
                        token.ThrowIfCancellationRequested();
                        if (row.Values.Length > document.Columns.Count)
                        {
                            CheckXlsxColumnLimit(row.Values.Length);
                            insert?.Dispose();
                            insert = null;
                            while (document.Columns.Count < row.Values.Length)
                            {
                                var name = UniqueName($"Column{document.Columns.Count + 1}", document.Columns.Select(c => c.Name));
                                using var alter = connection.CreateCommand();
                                alter.Transaction = transaction;
                                alter.CommandText = $"ALTER TABLE {Quote(document.TableName)} ADD COLUMN {Quote(name)} TEXT NOT NULL DEFAULT ''";
                                alter.ExecuteNonQuery();
                                document.Columns.Add(new CsvColumn(name));
                            }
                        }
                        if (insert is null)
                        {
                            insert = connection.CreateCommand();
                            insert.Transaction = transaction;
                            insert.CommandText = $"INSERT INTO {Quote(document.TableName)} ({ColumnNames(document)}) VALUES ({string.Join(',', document.Columns.Select((_, i) => "$v" + i))})";
                            for (var i = 0; i < document.Columns.Count; i++) insert.Parameters.Add("$v" + i, SqliteType.Text);
                            insert.Prepare();
                        }
                        for (var i = 0; i < document.Columns.Count; i++) insert.Parameters[i].Value = i < row.Values.Length ? row.Values[i] : "";
                        insert.ExecuteNonQuery();
                        document.RowCount++;
                    }
                    if (first is not null && !hasHeader) AddRow(first);
                    while (rows.MoveNext()) AddRow(rows.Current);
                }
                finally { insert?.Dispose(); }
                token.ThrowIfCancellationRequested();
                newMaps.Add(BuildMap(connection, transaction, document, new ViewSpec(), token));
                imported.Add(document);
            }
            finishStrings(connection, transaction);
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            for (var i = 0; i < imported.Count; i++)
            {
                documents.Add(imported[i]);
                maps[imported[i].TableName] = [newMaps[i]];
            }
            Checkpoint(connection);
            return imported;
        }
    }

    private static void CheckXlsxColumnLimit(int count)
    {
        // The bundled SQLite build permits 2,000 table columns; __id uses one.
        if (count > 1999) throw new InvalidDataException("このアプリで読み込める XLSX は 1 シートにつき 1,999 列までです。CSV に分割するか、対象列を減らしてください。");
    }
}

internal sealed record XlsxRow(long Number, string[] Values);
internal sealed record XlsxSheetSource(string Name, Func<IEnumerable<XlsxRow>> ReadRows);
