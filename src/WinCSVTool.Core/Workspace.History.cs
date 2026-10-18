using Microsoft.Data.Sqlite;

namespace WinCSVTool.Core;

public sealed partial class Workspace
{
    private UndoEntry? undoEntry;
    private UndoOperation? pendingUndo;
    internal sealed record UndoEntry(CsvDocument Document, string Description, string OldTable, string NewTable, long RowCount, bool Dirty);

    public bool HasIncompleteChange => pendingUndo is not null;
    public bool CanUndo => pendingUndo is null && undoEntry is { } entry && documents.Contains(entry.Document);
    public string? UndoDescription => CanUndo ? undoEntry!.Description : null;

    /// <summary>Records changed rows on disk. Call Complete only after a successful GUI operation.</summary>
    public UndoOperation BeginUndo(CsvDocument document, string description)
    {
        lock (gate)
        {
            if (!documents.Contains(document)) throw new ArgumentException("作業中の表ではありません。", nameof(document));
            if (pendingUndo is not null) throw new InvalidOperationException("編集履歴を記録中です。");
            var suffix = Guid.NewGuid().ToString("N");
            var entry = new UndoEntry(document, description, "__undo_old_" + suffix, "__undo_new_" + suffix, document.RowCount, document.IsDirty);
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            var names = "__id," + ColumnNames(document);
            var oldValues = "OLD.__id," + string.Join(',', document.Columns.Select(c => "OLD." + Quote(c.Name)));
            var oldInsert = $"INSERT OR IGNORE INTO {Quote(entry.OldTable)} SELECT {oldValues} WHERE NOT EXISTS (SELECT 1 FROM {Quote(entry.NewTable)} WHERE __id=OLD.__id);";
            command.CommandText = $"CREATE TABLE {Quote(entry.OldTable)} AS SELECT {names} FROM {Quote(document.TableName)} WHERE 0;" +
                $"CREATE UNIQUE INDEX {Quote(entry.OldTable + "_id")} ON {Quote(entry.OldTable)}(__id);" +
                $"CREATE TABLE {Quote(entry.NewTable)}(__id INTEGER PRIMARY KEY);" +
                $"CREATE TRIGGER {Quote(entry.OldTable + "_update")} BEFORE UPDATE ON {Quote(document.TableName)} BEGIN {oldInsert} END;" +
                $"CREATE TRIGGER {Quote(entry.OldTable + "_delete")} BEFORE DELETE ON {Quote(document.TableName)} BEGIN {oldInsert} END;" +
                $"CREATE TRIGGER {Quote(entry.OldTable + "_insert")} AFTER INSERT ON {Quote(document.TableName)} BEGIN INSERT OR IGNORE INTO {Quote(entry.NewTable)} VALUES(NEW.__id); END;";
            command.ExecuteNonQuery(); transaction.Commit();
            pendingUndo = new UndoOperation(this, entry);
            return pendingUndo;
        }
    }

    public sealed class UndoOperation : IDisposable
    {
        private Workspace? owner;
        private readonly UndoEntry entry;
        private bool completed;
        private bool disposalStarted;
        internal UndoOperation(Workspace workspace, UndoEntry value) { owner = workspace; entry = value; }
        public void Complete()
        {
            if (owner is null) throw new ObjectDisposedException(nameof(UndoOperation));
            if (disposalStarted) throw new InvalidOperationException("編集履歴の終了処理を開始しています。");
            completed = true;
        }
        public void Dispose()
        {
            var workspace = owner; if (workspace is null) return;
            lock (workspace.gate)
            {
                disposalStarted = true;
                using var connection = workspace.OpenConnection();
                using var transaction = connection.BeginTransaction();
                workspace.StopUndoTriggers(connection, transaction, entry);
                using var changed = connection.CreateCommand(); changed.Transaction = transaction;
                changed.CommandText = $"SELECT EXISTS(SELECT 1 FROM {Quote(entry.OldTable)}) OR EXISTS(SELECT 1 FROM {Quote(entry.NewTable)})";
                var hasChanges = Convert.ToInt64(changed.ExecuteScalar()) != 0;
                if (completed && hasChanges)
                {
                    workspace.DropUndoTables(connection, transaction, workspace.undoEntry);
                }
                else
                {
                    if (!completed && hasChanges) workspace.RestoreUndo(connection, transaction, entry, CancellationToken.None);
                    workspace.DropUndoTables(connection, transaction, entry);
                }
                transaction.Commit();
                if (completed && hasChanges) workspace.undoEntry = entry;
                else if (!completed && hasChanges) workspace.FinishUndoRestore(entry);
                workspace.pendingUndo = null;
                owner = null;
            }
        }
    }

    public CsvDocument? Undo(CancellationToken token = default)
    {
        lock (gate)
        {
            if (pendingUndo is not null) throw new InvalidOperationException("編集履歴を記録中です。");
            if (!CanUndo) return null;
            var entry = undoEntry!;
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var cancellation = ObserveCancellation(connection, token);
            RestoreUndo(connection, transaction, entry, token);
            DropUndoTables(connection, transaction, entry);
            token.ThrowIfCancellationRequested(); transaction.Commit();
            FinishUndoRestore(entry); undoEntry = null;
            return entry.Document;
        }
    }

    public void ClearUndo()
    {
        lock (gate)
        {
            if (pendingUndo is not null) throw new InvalidOperationException("編集履歴を記録中です。");
            using var connection = OpenConnection(); using var transaction = connection.BeginTransaction();
            DropUndoTables(connection, transaction, undoEntry); transaction.Commit(); undoEntry = null;
        }
    }

    private void RestoreUndo(SqliteConnection connection, SqliteTransaction transaction, UndoEntry entry, CancellationToken token)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = $"DELETE FROM {Quote(entry.Document.TableName)} WHERE __id IN (SELECT __id FROM {Quote(entry.NewTable)});" +
            $"INSERT OR REPLACE INTO {Quote(entry.Document.TableName)} SELECT * FROM {Quote(entry.OldTable)};";
        ExecuteCommand(command, token);
        DropDocumentMaps(connection, transaction, entry.Document, token);
    }

    private void DropDocumentMaps(SqliteConnection connection, SqliteTransaction transaction, CsvDocument document, CancellationToken token)
    {
        if (!maps.TryGetValue(document.TableName, out var list)) return;
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        foreach (var map in list)
        {
            command.CommandText = $"DROP TABLE IF EXISTS {Quote(map.Table)}";
            ExecuteCommand(command, token);
        }
    }

    private void FinishUndoRestore(UndoEntry entry)
    {
        entry.Document.RowCount = entry.RowCount; entry.Document.IsDirty = entry.Dirty;
        maps.Remove(entry.Document.TableName);
    }

    private void StopUndoTriggers(SqliteConnection connection, SqliteTransaction transaction, UndoEntry entry)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = string.Join(';', new[] { "_update", "_delete", "_insert" }.Select(s => $"DROP TRIGGER IF EXISTS {Quote(entry.OldTable + s)}"));
        command.ExecuteNonQuery();
    }

    private void DropUndoTables(SqliteConnection connection, SqliteTransaction transaction, UndoEntry? entry)
    {
        if (entry is null) return;
        StopUndoTriggers(connection, transaction, entry);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = $"DROP TABLE IF EXISTS {Quote(entry.OldTable)}; DROP TABLE IF EXISTS {Quote(entry.NewTable)};";
        command.ExecuteNonQuery();
    }
}
