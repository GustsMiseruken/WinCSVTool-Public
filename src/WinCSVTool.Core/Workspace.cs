using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace WinCSVTool.Core;

/// <summary>A disposable, disk-backed editing session. CSV data is never loaded as a complete in-memory table.</summary>
public sealed partial class Workspace : IDisposable
{
    private readonly List<CsvDocument> documents = [];
    private readonly Dictionary<string, List<ViewMap>> maps = [];
    private readonly object gate = new();
    private bool disposed;
    private int nextTable;
    public const int MaximumDocuments = 1024;
    private sealed record ViewMap(string Key, string Table, long Count);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    public string RootPath { get; }
    public string DatabasePath { get; }
    public IReadOnlyList<CsvDocument> Documents => documents;

    public Workspace(string? rootPath = null)
    {
        RootPath = Path.Combine(Path.GetFullPath(rootPath ?? Path.Combine(Path.GetTempPath(), "WinCSVTool")), "session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);
        DatabasePath = Path.Combine(RootPath, "workspace.db");
        try
        {
            InitializeRecovery();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            command.ExecuteNonQuery();
        }
        catch (Exception error)
        {
            // Only this constructor's newly allocated session child is eligible for cleanup.
            try { DisposeRecovery(); }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException) { ReportCleanupFailure(cleanupError); }
            try { Directory.Delete(RootPath, true); }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException) { ReportCleanupFailure(cleanupError); }
            if (cleanupFailures.Count > 0) error.Data["WinCSVTool.ResidualWorkFolder"] = RootPath;
            throw;
        }
    }

    public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    public SqliteConnection OpenConnection(bool readOnly = false)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 30
        }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA cache_size=-4096; PRAGMA temp_store=FILE; PRAGMA mmap_size=0; PRAGMA cache_spill=ON;";
            command.ExecuteNonQuery();
            connection.CreateFunction<string?, string?, bool>("regexp", (pattern, value) =>
                Regex.IsMatch(value ?? "", pattern ?? "", RegexOptions.CultureInvariant, RegexTimeout), true);
            connection.CreateFunction<string?, double?>("WCT_NUMBER", ParseNumber, true);
            connection.CreateFunction<string?, long?>("WCT_TIMESTAMP", ParseTimestamp, true);
            connection.CreateFunction<string?, long?>("WCT_BOOLEAN", ParseBoolean, true);
            connection.CreateFunction<string?, string?, bool>("WCT_CONTAINS", (value, search) => (value ?? "").Contains(search ?? "", StringComparison.OrdinalIgnoreCase), true);
            connection.CreateFunction<string?, string?, bool>("WCT_STARTS", (value, search) => (value ?? "").StartsWith(search ?? "", StringComparison.OrdinalIgnoreCase), true);
            connection.CreateFunction<string?, string?, bool>("WCT_ENDS", (value, search) => (value ?? "").EndsWith(search ?? "", StringComparison.OrdinalIgnoreCase), true);
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    public CsvDocument Import(string path, CsvOptions options, CancellationToken token = default, IProgress<ImportProgress>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        lock (gate) EnsureDocumentCapacity(1);
        CsvCodec.ValidateDelimiter(options.Delimiter);
        path = Path.GetFullPath(path);
        using var text = CompressedText.OpenRead(path, options.EncodingName, token);
        using var records = CsvCodec.Read(text.Reader, options.Delimiter, token).GetEnumerator();
        progress?.Report(new ImportProgress(0, 0, text.TotalBytes));
        token.ThrowIfCancellationRequested();
        var first = records.MoveNext() ? records.Current : null;
        var headers = first is null ? new[] { "Column1" } : options.HasHeader ? first : Enumerable.Range(1, first.Length).Select(i => $"Column{i}").ToArray();
        var document = NewDocument(Path.GetFileName(path), headers);
        document.SourcePath = path;
        document.EncodingName = text.EncodingName;
        document.HasHeader = options.HasHeader;
        document.Delimiter = options.Delimiter;
        lock (gate)
        {
            EnsureDocumentCapacity(1);
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            CreateTable(connection, transaction, document);
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = $"INSERT INTO {Quote(document.TableName)} ({ColumnNames(document)}) VALUES ({string.Join(',', document.Columns.Select((_, i) => "$v" + i))})";
            for (var i = 0; i < document.Columns.Count; i++) insert.Parameters.Add("$v" + i, SqliteType.Text);
            insert.Prepare();
            void AddRecord(string[] values)
            {
                token.ThrowIfCancellationRequested();
                if (values.Length > document.Columns.Count)
                    throw new CsvFormatException($"列数が {values.Length} です。先頭レコードの {document.Columns.Count} 列を超えています。読み込みを中止しました。", document.RowCount + (options.HasHeader ? 2 : 1));
                for (var i = 0; i < document.Columns.Count; i++) insert.Parameters[i].Value = i < values.Length ? values[i] : "";
                insert.ExecuteNonQuery();
                document.RowCount++;
                if ((document.RowCount & 8191) == 0)
                {
                    progress?.Report(new ImportProgress(document.RowCount, text.BytesRead, text.TotalBytes));
                    token.ThrowIfCancellationRequested();
                }
            }
            if (first is not null && !options.HasHeader) AddRecord(first);
            while (records.MoveNext()) AddRecord(records.Current);
            progress?.Report(new ImportProgress(document.RowCount, text.TotalBytes, text.TotalBytes));
            token.ThrowIfCancellationRequested();
            var map = BuildMap(connection, transaction, document, new ViewSpec(), token);
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            documents.Add(document);
            maps[document.TableName] = [map];
            Checkpoint(connection);
        }
        return document;
    }

    public CsvDocument CreateDocument(string name, IEnumerable<string> headers)
    {
        lock (gate)
        {
            EnsureDocumentCapacity(1);
            var document = NewDocument(name, headers);
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            CreateTable(connection, transaction, document);
            transaction.Commit();
            documents.Add(document);
            document.IsDirty = true;
            return document;
        }
    }

    private void EnsureDocumentCapacity(int additionalDocuments)
    {
        if (additionalDocuments < 0 || additionalDocuments > MaximumDocuments - documents.Count)
            throw new InvalidDataException($"1つの作業領域で開ける表は最大{MaximumDocuments:N0}個です。現在{documents.Count:N0}個のため、{additionalDocuments:N0}個の表を追加できません。不要なタブを閉じるか、別のウィンドウで開いてください。");
    }

    public List<CsvRow> ReadPage(CsvDocument document, ViewSpec view, long offset, int count, CancellationToken token = default)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (count is < 0 or > 10000) throw new ArgumentOutOfRangeException(nameof(count), "一度に取得できる行数は 0～10000 行です。");
        if (count == 0) return [];
        return EnumerateRowsRange(document, view, offset, count, token).ToList();
    }

    public IEnumerable<CsvRow> EnumerateRows(CsvDocument document, ViewSpec? view = null, CancellationToken token = default) =>
        EnumerateRowsRange(document, view ?? new ViewSpec(), 0, null, token);

    private IEnumerable<CsvRow> EnumerateRowsRange(CsvDocument document, ViewSpec view, long offset, long? count, CancellationToken token)
    {
        var map = EnsureMap(document, view, token);
        using var connection = OpenConnection(true);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT d.__id,{ColumnNames(document, "d")} FROM {Quote(map.Table)} m JOIN {Quote(document.TableName)} d ON d.__id=m.row_id WHERE m.ordinal >= $offset" +
            (count.HasValue ? " AND m.ordinal < $end" : "") + " ORDER BY m.ordinal";
        command.Parameters.AddWithValue("$offset", offset);
        if (count.HasValue) command.Parameters.AddWithValue("$end", checked(offset + count.Value));
        using var cancellation = ObserveCancellation(connection, token);
        using var reader = ReadCommand(command, token);
        while (ReadNext(reader, token))
        {
            token.ThrowIfCancellationRequested();
            var values = new string[document.Columns.Count];
            for (var i = 0; i < values.Length; i++) values[i] = reader.IsDBNull(i + 1) ? "" : reader.GetString(i + 1);
            yield return new CsvRow(reader.GetInt64(0), values);
        }
    }

    public long CountRows(CsvDocument document, ViewSpec view, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return view.IsEmpty ? document.RowCount : EnsureMap(document, view, token).Count;
    }

    /// <summary>Returns a SELECT of __id in display order; the disk order index is created if needed.</summary>
    public string GetViewRowIdsSql(CsvDocument document, ViewSpec view) => GetViewRangeSql(document, view, 0, null);

    public string GetViewRangeSql(CsvDocument document, ViewSpec view, long offset, long? count, CancellationToken token = default)
    {
        if (offset < 0 || count < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        var map = EnsureMap(document, view, token);
        return $"SELECT row_id AS __id FROM {Quote(map.Table)} WHERE ordinal >= {offset.ToString(CultureInfo.InvariantCulture)}" +
            (count.HasValue ? $" AND ordinal < {checked(offset + count.Value).ToString(CultureInfo.InvariantCulture)}" : "") + " ORDER BY ordinal";
    }

    public void UpdateCell(CsvDocument document, long id, int column, string value)
    {
        ValidateColumn(document, column);
        lock (gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE {Quote(document.TableName)} SET {Quote(document.Columns[column].Name)}=$value WHERE __id=$id";
            command.Parameters.AddWithValue("$value", value);
            command.Parameters.AddWithValue("$id", id);
            if (command.ExecuteNonQuery() != 0) { document.IsDirty = true; InvalidateFilteredViews(connection, document); }
        }
    }

    public long AppendRow(CsvDocument document, IReadOnlyList<string>? values = null)
    {
        if (values is not null && values.Count > document.Columns.Count) throw new ArgumentException("列数を超える値があります。", nameof(values));
        lock (gate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"INSERT INTO {Quote(document.TableName)} ({ColumnNames(document)}) VALUES ({string.Join(',', document.Columns.Select((_, i) => "$v" + i))}); SELECT last_insert_rowid();";
            for (var i = 0; i < document.Columns.Count; i++) command.Parameters.AddWithValue("$v" + i, values is not null && i < values.Count ? values[i] : "");
            var id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            maps.TryGetValue(document.TableName, out var existing);
            var map = existing?.FirstOrDefault(x => x.Key == "base");
            if (map is not null)
            {
                using var append = connection.CreateCommand();
                append.Transaction = transaction;
                append.CommandText = $"INSERT INTO {Quote(map.Table)}(ordinal,row_id) VALUES($ordinal,$id)";
                append.Parameters.AddWithValue("$ordinal", document.RowCount);
                append.Parameters.AddWithValue("$id", id);
                append.ExecuteNonQuery();
            }
            transaction.Commit();
            InvalidateFilteredViews(connection, document);
            if (map is not null) existing![existing.IndexOf(map)] = map with { Count = document.RowCount + 1 };
            document.RowCount++;
            document.IsDirty = true;
            return id;
        }
    }

    public void DeleteRows(CsvDocument document, IEnumerable<long> ids)
    {
        lock (gate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {Quote(document.TableName)} WHERE __id=$id";
            command.Parameters.Add("$id", SqliteType.Integer);
            long deleted = 0;
            foreach (var id in ids) { command.Parameters[0].Value = id; deleted += command.ExecuteNonQuery(); }
            transaction.Commit();
            if (deleted > 0)
            {
                document.RowCount -= deleted;
                document.IsDirty = true;
                DropMaps(connection, document);
            }
        }
    }

    public void AddColumn(CsvDocument document, string name, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        lock (gate) InsertColumn(document, document.Columns.Count, name, token);
    }

    /// <summary>Inserts at index 0..Count; interior insertion rebuilds the table on disk in one transaction.</summary>
    public void InsertColumn(CsvDocument document, int index, string name, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            ValidateColumnStructureDocument(document);
            if ((uint)index > document.Columns.Count) throw new ArgumentOutOfRangeException(nameof(index));
            if (document.Columns.Count >= 1999) throw new InvalidDataException("このアプリの表は最大1,999列です。列を削除してから追加してください。");
            var sqlName = UniqueName(name, document.Columns.Select(c => c.Name));
            var columns = document.Columns.ToList();
            columns.Insert(index, new CsvColumn(sqlName) { HeaderName = name });
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var cancellation = ObserveCancellation(connection, token);
            DropUndoTables(connection, transaction, undoEntry);
            if (index == document.Columns.Count)
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = $"ALTER TABLE {Quote(document.TableName)} ADD COLUMN {Quote(sqlName)} TEXT NOT NULL DEFAULT ''";
                ExecuteCommand(command, token);
            }
            else RebuildColumns(connection, transaction, document, columns, document.Columns, token);
            CommitColumnStructure(connection, transaction, document, columns, token);
        }
    }

    public void DeleteColumn(CsvDocument document, int column, CancellationToken token = default) => DeleteColumns(document, [column], token);

    /// <summary>Deletes distinct selected columns atomically, keeping at least one user column.</summary>
    public void DeleteColumns(CsvDocument document, IReadOnlyList<int> columns, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(columns);
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            ValidateColumnStructureDocument(document);
            var selected = columns.Distinct().ToHashSet();
            if (selected.Count == 0) throw new ArgumentException("削除する列を選択してください。", nameof(columns));
            if (selected.Any(i => (uint)i >= document.Columns.Count)) throw new ArgumentOutOfRangeException(nameof(columns));
            if (selected.Count >= document.Columns.Count) throw new InvalidOperationException("最後の列は削除できません。少なくとも1列を残してください。");
            var remaining = document.Columns.Where((_, i) => !selected.Contains(i)).ToArray();
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var cancellation = ObserveCancellation(connection, token);
            DropUndoTables(connection, transaction, undoEntry);
            RebuildColumns(connection, transaction, document, remaining, remaining, token);
            CommitColumnStructure(connection, transaction, document, remaining, token);
        }
    }

    public void RenameColumn(CsvDocument document, int column, string name, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            ValidateColumnStructureDocument(document); ValidateColumn(document, column);
            var old = document.Columns[column];
            var sqlName = UniqueName(name, document.Columns.Where((_, i) => i != column).Select(c => c.Name));
            if (old.Name == sqlName && old.HeaderName == name) return;
            var columns = document.Columns.ToList();
            columns[column] = new CsvColumn(sqlName) { HeaderName = name, Kind = old.Kind };
            using var connection = OpenConnection(); using var transaction = connection.BeginTransaction();
            using var cancellation = ObserveCancellation(connection, token);
            DropUndoTables(connection, transaction, undoEntry);
            if (old.Name != sqlName)
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = $"ALTER TABLE {Quote(document.TableName)} RENAME COLUMN {Quote(old.Name)} TO {Quote(sqlName)}";
                ExecuteCommand(command, token);
            }
            CommitColumnStructure(connection, transaction, document, columns, token);
        }
    }

    private void ValidateColumnStructureDocument(CsvDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!documents.Contains(document)) throw new InvalidOperationException("この作業領域の表ではありません。");
        if (pendingUndo is not null) throw new InvalidOperationException("編集履歴を記録中のため、列構造を変更できません。");
    }

    private static void RebuildColumns(SqliteConnection connection, SqliteTransaction transaction, CsvDocument document,
        IReadOnlyList<CsvColumn> targetColumns, IReadOnlyList<CsvColumn> copyColumns, CancellationToken token)
    {
        var temporary = "__columns_" + Guid.NewGuid().ToString("N");
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = $"CREATE TABLE {Quote(temporary)}(__id INTEGER PRIMARY KEY,{string.Join(',', targetColumns.Select(c => Quote(c.Name) + " TEXT NOT NULL DEFAULT ''"))})";
        ExecuteCommand(command, token);
        var names = "__id," + string.Join(',', copyColumns.Select(c => Quote(c.Name)));
        command.CommandText = $"INSERT INTO {Quote(temporary)}({names}) SELECT {names} FROM {Quote(document.TableName)}";
        ExecuteCommand(command, token);
        command.CommandText = $"DROP TABLE {Quote(document.TableName)}; ALTER TABLE {Quote(temporary)} RENAME TO {Quote(document.TableName)}";
        ExecuteCommand(command, token);
    }

    private void CommitColumnStructure(SqliteConnection connection, SqliteTransaction transaction, CsvDocument document,
        IReadOnlyList<CsvColumn> columns, CancellationToken token)
    {
        DropDocumentMaps(connection, transaction, document, token);
        WriteRecoveryCheckpoint(connection, transaction, document, replacementColumns: columns);
        token.ThrowIfCancellationRequested(); transaction.Commit();
        document.Columns.Clear(); document.Columns.AddRange(columns);
        document.IsDirty = true; maps.Remove(document.TableName); undoEntry = null;
    }

    public void Export(CsvDocument document, string path, CsvOptions options, ViewSpec? view = null, CancellationToken token = default)
    {
        ValidateExportDestination(path);
        ExportRows(document, path, options, EnumerateRows(document, view, token), view is null && !options.EscapeSpreadsheetFormulas, token);
    }

    public void Export(CsvDocument document, string path, CsvOptions options, RowScope scope, CancellationToken token = default)
    {
        ValidateScope(document, scope);
        ValidateExportDestination(path);
        ExportRows(document, path, options, EnumerateRows(document, scope, token), false, token);
    }

    private static void ExportRows(CsvDocument document, string path, CsvOptions options, IEnumerable<CsvRow> rows, bool markSaved, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        CsvCodec.ValidateDelimiter(options.Delimiter);
        path = Path.GetFullPath(path);
        var encodingName = options.EncodingName.Equals("auto", StringComparison.OrdinalIgnoreCase) ? document.EncodingName : options.EncodingName;
        ValidateLogDestination(document, path);
        var encoding = EncodingDetector.Resolve(encodingName, options.WriteBom);
        using var output = new AtomicFile(path);
        using (var compressed = CompressedText.OpenWrite(output.Stream, path))
        using (var writer = new StreamWriter(compressed, encoding, 64 * 1024, true))
        {
            if (options.HasHeader) CsvCodec.WriteRecord(writer, document.Columns.Select(x => x.HeaderName), options.Delimiter, options.EscapeSpreadsheetFormulas);
            foreach (var row in rows) CsvCodec.WriteRecord(writer, row.Values, options.Delimiter, options.EscapeSpreadsheetFormulas);
            token.ThrowIfCancellationRequested();
            writer.Flush();
        }
        output.Commit(token);
        if (markSaved)
        {
            document.WorkbookId = null;
            document.WorksheetName = null;
            document.WorkbookSourcePath = null;
            document.SourcePath = path;
            document.DisplayName = Path.GetFileName(path);
            document.EncodingName = encoding.WebName;
            document.HasHeader = options.HasHeader;
            document.Delimiter = options.Delimiter;
            document.IsDirty = false;
        }
    }

    public FindMatch? Find(CsvDocument document, ViewSpec view, int? column, string search, bool regex, long startOffset = 0, CancellationToken token = default)
    {
        if (column.HasValue) ValidateColumn(document, column.Value);
        if (startOffset < 0) throw new ArgumentOutOfRangeException(nameof(startOffset));
        var pattern = regex ? new Regex(search, RegexOptions.CultureInvariant, RegexTimeout) : null;
        var offset = startOffset;
        foreach (var row in EnumerateRowsRange(document, view, startOffset, null, token))
        {
            for (var i = column ?? 0; i <= (column ?? (document.Columns.Count - 1)); i++)
                if (pattern?.IsMatch(row.Values[i]) ?? row.Values[i].Contains(search, StringComparison.OrdinalIgnoreCase))
                    return new FindMatch(row.Id, i, row.Values[i], offset);
            offset++;
        }
        return null;
    }

    public long Replace(CsvDocument document, ViewSpec view, int? column, string search, string replacement, bool regex, CancellationToken token = default)
    {
        using var scope = CaptureScope(document, view, token);
        return Replace(document, scope, column, search, replacement, regex, token);
    }

    public long Replace(CsvDocument document, RowScope scope, int? column, string search, string replacement, bool regex, CancellationToken token = default)
    {
        ValidateScope(document, scope);
        if (column.HasValue) ValidateColumn(document, column.Value);
        if (!regex && search.Length == 0) throw new ArgumentException("検索文字列を入力してください。", nameof(search));
        token.ThrowIfCancellationRequested();
        var pattern = regex ? new Regex(search, RegexOptions.CultureInvariant, RegexTimeout) : null;
        lock (gate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var select = connection.CreateCommand();
            select.Transaction = transaction;
            select.CommandText = $"SELECT d.__id,{ColumnNames(document, "d")} FROM {Quote(scope.Table)} m JOIN {Quote(document.TableName)} d ON d.__id=m.row_id ORDER BY m.ordinal";
            using var cancellation = ObserveCancellation(connection, token);
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.Parameters.Add("$value", SqliteType.Text);
            update.Parameters.Add("$id", SqliteType.Integer);
            long changed = 0;
            using (var reader = ReadCommand(select, token))
            {
                while (ReadNext(reader, token))
                {
                    token.ThrowIfCancellationRequested();
                    for (var i = column ?? 0; i <= (column ?? (document.Columns.Count - 1)); i++)
                    {
                        var original = reader.GetString(i + 1);
                        var result = pattern is not null ? pattern.Replace(original, replacement) : original.Replace(search, replacement, StringComparison.OrdinalIgnoreCase);
                        if (result == original) continue;
                        update.CommandText = $"UPDATE {Quote(document.TableName)} SET {Quote(document.Columns[i].Name)}=$value WHERE __id=$id";
                        update.Parameters[0].Value = result;
                        update.Parameters[1].Value = reader.GetInt64(0);
                        ExecuteCommand(update, token);
                        changed++;
                    }
                }
            }
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            if (changed > 0) { document.IsDirty = true; InvalidateFilteredViews(connection, document); }
            return changed;
        }
    }

    public void InvalidateView(CsvDocument document)
    {
        lock (gate) { using var connection = OpenConnection(); DropMaps(connection, document); }
    }

    internal void InvalidateContentView(CsvDocument document)
    {
        lock (gate) { using var connection = OpenConnection(); InvalidateFilteredViews(connection, document); }
    }

    public void CloseDocument(CsvDocument document)
    {
        lock (gate)
        {
            using var connection = OpenConnection();
            DropMaps(connection, document);
            using var command = connection.CreateCommand();
            command.CommandText = $"DROP TABLE IF EXISTS {Quote(document.TableName)}";
            command.ExecuteNonQuery();
            documents.Remove(document);
        }
    }

    private ViewMap EnsureMap(CsvDocument document, ViewSpec view, CancellationToken token)
    {
        lock (gate)
        {
            if (!documents.Contains(document)) throw new InvalidOperationException("この作業領域のタブではありません。");
            var key = ViewKey(document, view);
            if (maps.TryGetValue(document.TableName, out var existing) && existing.FirstOrDefault(x => x.Key == key) is { } found) return found;
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var map = BuildMap(connection, transaction, document, view, token);
            transaction.Commit();
            existing ??= [];
            // Keep one base map and at most two filtered/sorted maps per tab.
            while (existing.Count(x => x.Key != "base") >= 2)
            {
                var stale = existing.First(x => x.Key != "base");
                DropMap(connection, stale);
                existing.Remove(stale);
            }
            existing.Add(map);
            maps[document.TableName] = existing;
            return map;
        }
    }

    private static ViewMap BuildMap(SqliteConnection connection, SqliteTransaction transaction, CsvDocument document, ViewSpec view, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var key = ViewKey(document, view);
        var table = "map_" + document.TableName + "_" + (key == "base" ? "base" : key);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 0;
        command.CommandText = $"CREATE TABLE {Quote(table)}(ordinal INTEGER PRIMARY KEY,row_id INTEGER NOT NULL)";
        command.ExecuteNonQuery();
        var where = BuildWhere(document, view, command);
        var order = BuildOrder(document, view);
        command.CommandText = $"INSERT INTO {Quote(table)}(ordinal,row_id) SELECT ROW_NUMBER() OVER(ORDER BY {order})-1,__id FROM {Quote(document.TableName)}{where} ORDER BY {order}";
        using var cancellation = ObserveCancellation(connection, token);
        ExecuteCommand(command, token);
        token.ThrowIfCancellationRequested();
        command.Parameters.Clear();
        command.CommandText = $"SELECT COUNT(*) FROM {Quote(table)}";
        long count;
        try { count = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture); }
        catch (SqliteException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        return new ViewMap(key, table, count);
    }

    private static string BuildWhere(CsvDocument document, ViewSpec view, SqliteCommand command)
    {
        var filters = new List<string>();
        foreach (var filter in view.Filters)
        {
            ValidateColumn(document, filter.Column);
            var column = Quote(document.Columns[filter.Column].Name);
            var parameter = "$f" + filters.Count;
            command.Parameters.AddWithValue(parameter, filter.Value);
            var expression = filter.Operator switch
            {
                FilterOperator.Contains => $"WCT_CONTAINS({column},{parameter})",
                FilterOperator.StartsWith => $"WCT_STARTS({column},{parameter})",
                FilterOperator.EndsWith => $"WCT_ENDS({column},{parameter})",
                FilterOperator.Equals => $"{column}={parameter} COLLATE NOCASE",
                FilterOperator.NotEquals => $"{column}<>{parameter} COLLATE NOCASE",
                FilterOperator.Empty => $"{column}=''",
                FilterOperator.NotEmpty => $"{column}<>''",
                FilterOperator.Regex => $"{column} REGEXP {parameter}",
                FilterOperator.GreaterThan => Comparison(document.Columns[filter.Column], column, parameter, ">"),
                FilterOperator.LessThan => Comparison(document.Columns[filter.Column], column, parameter, "<"),
                _ => throw new ArgumentOutOfRangeException(nameof(view))
            };
            filters.Add(expression);
        }
        return filters.Count == 0 ? "" : " WHERE " + string.Join(" AND ", filters);
    }

    private static string Comparison(CsvColumn column, string value, string parameter, string op) => column.Kind switch
    {
        DataKind.Text => $"{value} {op} {parameter} COLLATE NOCASE",
        DataKind.Timestamp => $"WCT_TIMESTAMP({value}) {op} WCT_TIMESTAMP({parameter})",
        DataKind.Boolean => $"WCT_BOOLEAN({value}) {op} WCT_BOOLEAN({parameter})",
        DataKind.Number => $"WCT_NUMBER({value}) {op} WCT_NUMBER({parameter})",
        _ => $"CASE WHEN WCT_NUMBER({parameter}) IS NOT NULL THEN WCT_NUMBER({value}) {op} WCT_NUMBER({parameter}) WHEN WCT_TIMESTAMP({parameter}) IS NOT NULL THEN WCT_TIMESTAMP({value}) {op} WCT_TIMESTAMP({parameter}) ELSE {value} {op} {parameter} COLLATE NOCASE END"
    };

    private static string BuildOrder(CsvDocument document, ViewSpec view)
    {
        var sorts = new List<string>();
        foreach (var sort in view.Sorts)
        {
            ValidateColumn(document, sort.Column);
            var column = document.Columns[sort.Column];
            var name = Quote(column.Name);
            var direction = sort.Descending ? " DESC" : " ASC";
            var parsed = column.Kind switch { DataKind.Number => $"WCT_NUMBER({name})", DataKind.Timestamp => $"WCT_TIMESTAMP({name})", DataKind.Boolean => $"WCT_BOOLEAN({name})", _ => null };
            if (parsed is not null) { sorts.Add($"({parsed} IS NULL) ASC"); sorts.Add(parsed + direction); }
            else if (column.Kind == DataKind.Auto)
            {
                sorts.Add($"CASE WHEN WCT_NUMBER({name}) IS NOT NULL THEN 0 WHEN WCT_TIMESTAMP({name}) IS NOT NULL THEN 1 ELSE 2 END ASC");
                sorts.Add($"WCT_NUMBER({name})" + direction);
                sorts.Add($"WCT_TIMESTAMP({name})" + direction);
                sorts.Add(name + " COLLATE NOCASE" + direction);
            }
            else sorts.Add(name + " COLLATE NOCASE" + direction);
        }
        sorts.Add("__id ASC");
        return string.Join(',', sorts);
    }

    private static string ViewKey(CsvDocument document, ViewSpec view)
    {
        if (view.IsEmpty) return "base";
        var text = System.Text.Json.JsonSerializer.Serialize(new { view.Filters, view.Sorts, Kinds = document.Columns.Select(c => c.Kind).ToArray() });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..20];
    }

    private CsvDocument NewDocument(string name, IEnumerable<string> headers)
    {
        var document = new CsvDocument { TableName = "t" + Interlocked.Increment(ref nextTable), DisplayName = name };
        var used = new List<string>();
        foreach (var header in headers)
        {
            var sqlName = UniqueName(header, used);
            used.Add(sqlName);
            document.Columns.Add(new CsvColumn(sqlName) { HeaderName = header });
        }
        if (document.Columns.Count == 0) document.Columns.Add(new CsvColumn("Column1"));
        return document;
    }

    private static string UniqueName(string name, IEnumerable<string> used)
    {
        var names = new HashSet<string>(used, StringComparer.OrdinalIgnoreCase) { "__id" };
        var baseName = string.IsNullOrWhiteSpace(name) ? "Column" + (names.Count) : name.Replace('\0', '_');
        var candidate = baseName;
        var suffix = 2;
        while (names.Contains(candidate)) candidate = baseName + "_" + suffix++;
        return candidate;
    }

    private static string ColumnNames(CsvDocument document, string? alias = null) => string.Join(',', document.Columns.Select(c => (alias is null ? "" : alias + ".") + Quote(c.Name)));
    private static void ValidateColumn(CsvDocument document, int column) { if ((uint)column >= document.Columns.Count) throw new ArgumentOutOfRangeException(nameof(column)); }
    private static void CreateTable(SqliteConnection connection, SqliteTransaction transaction, CsvDocument document)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"CREATE TABLE {Quote(document.TableName)}(__id INTEGER PRIMARY KEY,{string.Join(',', document.Columns.Select(c => Quote(c.Name) + " TEXT NOT NULL DEFAULT ''"))})";
        command.ExecuteNonQuery();
    }
    private static void DropMap(SqliteConnection connection, ViewMap map)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"DROP TABLE IF EXISTS {Quote(map.Table)}";
        command.ExecuteNonQuery();
    }
    private void DropMaps(SqliteConnection connection, CsvDocument document)
    {
        if (!maps.Remove(document.TableName, out var list)) return;
        foreach (var map in list) DropMap(connection, map);
    }
    private void InvalidateFilteredViews(SqliteConnection connection, CsvDocument document)
    {
        if (!maps.TryGetValue(document.TableName, out var list)) return;
        foreach (var map in list.Where(m => m.Key != "base").ToArray()) { DropMap(connection, map); list.Remove(map); }
    }
    private static double? ParseNumber(string? value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;
    private static long? ParseTimestamp(string? value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? time.UtcTicks : null;
    private static long? ParseBoolean(string? value) => value?.Trim().ToLowerInvariant() switch { "true" or "yes" or "1" => 1, "false" or "no" or "0" => 0, _ => null };
    internal static IDisposable ObserveCancellation(SqliteConnection connection, CancellationToken token) => new SqlCancellation(connection, token);
    private sealed class SqlCancellation : IDisposable
    {
        private readonly SqliteConnection connection;
        private readonly CancellationTokenRegistration registration;
        public SqlCancellation(SqliteConnection connection, CancellationToken token)
        {
            this.connection = connection;
            SQLitePCL.raw.sqlite3_progress_handler(connection.Handle!, 2000, _ => token.IsCancellationRequested ? 1 : 0, null);
            registration = token.Register(() => SQLitePCL.raw.sqlite3_interrupt(connection.Handle!));
        }
        public void Dispose()
        {
            registration.Dispose();
            SQLitePCL.raw.sqlite3_progress_handler(connection.Handle!, 0, null, null);
        }
    }
    private static SqliteDataReader ReadCommand(SqliteCommand command, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { return command.ExecuteReader(); }
        catch (SqliteException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
    }
    private static bool ReadNext(SqliteDataReader reader, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { return reader.Read(); }
        catch (SqliteException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
    }
    private static void ExecuteCommand(SqliteCommand command, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { command.ExecuteNonQuery(); }
        catch (SqliteException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
    }
    private static void Checkpoint(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        command.ExecuteNonQuery();
    }
    private static void SkipMatchingPreamble(FileStream file, Encoding encoding)
    {
        byte[] preamble = encoding.CodePage switch { 65001 => [0xef, 0xbb, 0xbf], 1200 => [0xff, 0xfe], 1201 => [0xfe, 0xff], _ => [] };
        if (preamble.Length == 0) return;
        var bytes = new byte[preamble.Length];
        var count = file.Read(bytes, 0, bytes.Length);
        file.Position = count == preamble.Length && bytes.SequenceEqual(preamble) ? preamble.Length : 0;
    }

    public void Dispose()
    {
        if (disposed) return;
        DisposeRecovery();
        disposed = true;
        documents.Clear();
        maps.Clear();
        // RootPath is always a newly-created session child, never the caller's directory.
        try { Directory.Delete(RootPath, true); }
        catch (IOException ex) { ReportCleanupFailure(ex); }
        catch (UnauthorizedAccessException ex) { ReportCleanupFailure(ex); }
        GC.SuppressFinalize(this);
    }
}
