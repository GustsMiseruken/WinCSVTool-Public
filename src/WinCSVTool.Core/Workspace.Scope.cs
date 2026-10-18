using Microsoft.Data.Sqlite;

namespace WinCSVTool.Core;

/// <summary>A disposable, disk-backed snapshot of row membership and display order. Cell values are read live.</summary>
public sealed class RowScope : IDisposable
{
    internal Workspace Owner { get; }
    internal string Table { get; }
    internal bool IsDisposed { get; private set; }
    public CsvDocument Document { get; }
    public long Count { get; }
    internal RowScope(Workspace owner, CsvDocument document, string table, long count)
        => (Owner, Document, Table, Count) = (owner, document, table, count);
    public void Dispose()
    {
        if (IsDisposed) return;
        Owner.ReleaseScope(this);
        IsDisposed = true;
    }
}

public sealed partial class Workspace
{
    public RowScope CaptureScope(CsvDocument document, ViewSpec view, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(view);
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            var map = EnsureMap(document, view, token);
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var cancellation = ObserveCancellation(connection, token);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var table = "scope_" + Guid.NewGuid().ToString("N");
            command.CommandText = $"CREATE TABLE {Quote(table)}(ordinal INTEGER PRIMARY KEY,row_id INTEGER NOT NULL)";
            ExecuteCommand(command, token);
            command.CommandText = $"INSERT INTO {Quote(table)} SELECT ordinal,row_id FROM {Quote(map.Table)} ORDER BY ordinal";
            ExecuteCommand(command, token);
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return new RowScope(this, document, table, map.Count);
        }
    }

    /// <summary>Captures explicit selected row IDs without reapplying a possibly changed filter or sort.</summary>
    public RowScope CaptureScope(CsvDocument document, IEnumerable<long> selectedRowIds, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(selectedRowIds);
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!documents.Contains(document)) throw new InvalidOperationException("この作業領域のタブではありません。");
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var cancellation = ObserveCancellation(connection, token);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var table = "scope_" + Guid.NewGuid().ToString("N");
            command.CommandText = $"CREATE TABLE {Quote(table)}(ordinal INTEGER PRIMARY KEY,row_id INTEGER NOT NULL UNIQUE)";
            ExecuteCommand(command, token);
            command.CommandText = $"INSERT OR IGNORE INTO {Quote(table)}(ordinal,row_id) SELECT $ordinal,$id WHERE EXISTS(SELECT 1 FROM {Quote(document.TableName)} WHERE __id=$id)";
            command.Parameters.Add("$ordinal", SqliteType.Integer);
            command.Parameters.Add("$id", SqliteType.Integer);
            long count = 0;
            foreach (var id in selectedRowIds)
            {
                token.ThrowIfCancellationRequested();
                command.Parameters[0].Value = count;
                command.Parameters[1].Value = id;
                try { count += command.ExecuteNonQuery(); }
                catch (SqliteException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
            }
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return new RowScope(this, document, table, count);
        }
    }

    internal void ValidateScope(CsvDocument document, RowScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ObjectDisposedException.ThrowIf(scope.IsDisposed, scope);
        ObjectDisposedException.ThrowIf(disposed, this);
        if (scope.Owner != this || scope.Document != document || !documents.Contains(document))
            throw new InvalidOperationException("この表の行スコープではありません。");
    }

    internal string GetScopeRowIdsSql(CsvDocument document, RowScope scope)
    {
        ValidateScope(document, scope);
        return $"SELECT row_id AS __id FROM {Quote(scope.Table)} ORDER BY ordinal";
    }

    internal void ReleaseScope(RowScope scope)
    {
        lock (gate)
        {
            if (disposed) return;
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = $"DROP TABLE IF EXISTS {Quote(scope.Table)}";
            command.ExecuteNonQuery();
        }
    }

    public IEnumerable<CsvRow> EnumerateRows(CsvDocument document, RowScope scope, CancellationToken token = default)
    {
        ValidateScope(document, scope);
        using var connection = OpenConnection(true);
        using var cancellation = ObserveCancellation(connection, token);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT d.__id,{ColumnNames(document, "d")} FROM {Quote(scope.Table)} s JOIN {Quote(document.TableName)} d ON d.__id=s.row_id ORDER BY s.ordinal";
        using var reader = ReadCommand(command, token);
        while (ReadNext(reader, token))
        {
            var values = new string[document.Columns.Count];
            for (var i = 0; i < values.Length; i++) values[i] = reader.IsDBNull(i + 1) ? "" : reader.GetString(i + 1);
            yield return new CsvRow(reader.GetInt64(0), values);
        }
    }
}
