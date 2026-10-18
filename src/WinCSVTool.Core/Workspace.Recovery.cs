using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace WinCSVTool.Core;

public sealed record WorkspaceRecoveryCandidate(string Path, DateTime LastWrittenUtc, bool HasCheckpoint, string? Problem);

public sealed partial class Workspace
{
    private FileStream? recoveryLock;
    private readonly List<string> cleanupFailures = [];
    public IReadOnlyList<string> CleanupFailures => cleanupFailures;
    private const string RecoveryMarker = "session.marker";
    private const int MaximumRecoveryMetadataBytes = 16 * 1024 * 1024;
    private sealed record RecoveryColumn(string Name, string HeaderName, DataKind Kind);
    private sealed record RecoveryDocument(string TableName, string DisplayName, string? SourcePath, string? WorkbookId,
        string? WorksheetName, string? WorkbookSourcePath, string EncodingName, bool HasHeader, char Delimiter, RecoveryColumn[] Columns);

    private void InitializeRecovery()
    {
        if (OperatingSystem.IsWindows())
        {
            var identity = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException("現在のWindowsユーザーを取得できません。");
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(RootPath).SetAccessControl(security);
        }
        else File.SetUnixFileMode(RootPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        recoveryLock = new FileStream(Path.Combine(RootPath, "session.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Delete);
        File.WriteAllText(Path.Combine(RootPath, RecoveryMarker), "WinCSVTool recovery 1\n" + Path.GetFileName(RootPath) + "\n");
    }

    public void SaveRecoveryCheckpoint()
    {
        lock (gate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            WriteRecoveryCheckpoint(connection, transaction); transaction.Commit();
        }
    }

    private void WriteRecoveryCheckpoint(SqliteConnection connection, SqliteTransaction transaction,
        CsvDocument? changedDocument = null, IReadOnlyList<CsvColumn>? addedColumns = null, IReadOnlyList<CsvColumn>? replacementColumns = null)
    {
        if (documents.Count > MaximumDocuments)
            throw new InvalidDataException($"復旧用に保存できる表は最大{MaximumDocuments}個です。直前の復旧情報を保持しています。");
        var state = documents.Select(d => new RecoveryDocument(d.TableName, d.DisplayName, d.SourcePath, d.WorkbookId,
            d.WorksheetName, d.WorkbookSourcePath, d.EncodingName, d.HasHeader, d.Delimiter,
            (d == changedDocument && replacementColumns is not null ? replacementColumns :
                d == changedDocument && addedColumns is not null ? d.Columns.Concat(addedColumns) : d.Columns)
                .Select(c => new RecoveryColumn(c.Name, c.HeaderName, c.Kind)).ToArray())).ToArray();
        var json = JsonSerializer.Serialize(state);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumRecoveryMetadataBytes)
            throw new InvalidDataException("復旧用の文書情報が上限16MBを超えています。");
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "CREATE TABLE IF NOT EXISTS __wct_recovery(id INTEGER PRIMARY KEY CHECK(id=1), payload TEXT NOT NULL); INSERT OR REPLACE INTO __wct_recovery VALUES(1,$json);";
        command.Parameters.AddWithValue("$json", json); command.ExecuteNonQuery();
    }

    public static IReadOnlyList<WorkspaceRecoveryCandidate> FindRecoveryCandidates(string? workFolder = null)
    {
        var parent = Path.GetFullPath(workFolder ?? Path.Combine(Path.GetTempPath(), "WinCSVTool"));
        if (!Directory.Exists(parent)) return [];
        var result = new List<WorkspaceRecoveryCandidate>();
        foreach (var path in Directory.EnumerateDirectories(parent, "session-*", SearchOption.TopDirectoryOnly))
        {
            if (!IsRecoverySession(path)) continue;
            try
            {
                using var sessionLock = AcquireRecoveryLock(path);
                using var connection = OpenRecoveryDatabase(path);
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT length(CAST(payload AS BLOB)) FROM __wct_recovery WHERE id=1";
                var size = Convert.ToInt64(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
                result.Add(new(path, Directory.GetLastWriteTimeUtc(path), size > 0 && size <= MaximumRecoveryMetadataBytes,
                    size > MaximumRecoveryMetadataBytes ? "復旧用の文書情報が上限を超えています。" : null));
            }
            catch (IOException) { /* A live process owns the lock. Never offer or delete it. */ }
            catch (Exception ex) when (ex is SqliteException or UnauthorizedAccessException or InvalidDataException)
            { result.Add(new(path, Directory.GetLastWriteTimeUtc(path), false, ex.Message)); }
        }
        return result.OrderByDescending(c => c.LastWrittenUtc).ToArray();
    }

    public static Workspace Recover(WorkspaceRecoveryCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!IsRecoverySession(candidate.Path)) throw new InvalidDataException("WinCSVToolの復旧対象ではないか、安全に確認できないフォルダーです。");
        var sessionLock = AcquireRecoveryLock(candidate.Path);
        try { return new Workspace(candidate.Path, sessionLock); }
        catch (SqliteException ex) { sessionLock.Dispose(); throw new InvalidDataException("復旧用の作業DBがないか、破損しています。データは削除していません。", ex); }
        catch { sessionLock.Dispose(); throw; }
    }

    private Workspace(string existingPath, FileStream sessionLock)
    {
        RootPath = Path.GetFullPath(existingPath); DatabasePath = Path.Combine(RootPath, "workspace.db"); recoveryLock = sessionLock;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT length(CAST(payload AS BLOB)) FROM __wct_recovery WHERE id=1";
        var length = Convert.ToInt64(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        if (length is <= 0 or > MaximumRecoveryMetadataBytes) throw new InvalidDataException("復旧用の文書情報がないか、上限16MBを超えています。");
        command.CommandText = "SELECT payload FROM __wct_recovery WHERE id=1";
        if (command.ExecuteScalar() is not string json) throw new InvalidDataException("復旧用の文書情報が文字列ではありません。");
        RecoveryDocument[] state;
        try { state = JsonSerializer.Deserialize<RecoveryDocument[]>(json) ?? throw new InvalidDataException("復旧情報が空です。"); }
        catch (JsonException ex) { throw new InvalidDataException("復旧用の文書情報が破損しています。", ex); }
        if (state.Length > MaximumDocuments) throw new InvalidDataException("復旧する表の数が上限を超えています。");
        foreach (var saved in state)
        {
            if (saved is null || saved.TableName is null || saved.DisplayName is null || saved.EncodingName is null ||
                !Regex.IsMatch(saved.TableName, "^t[1-9][0-9]{0,8}$", RegexOptions.CultureInvariant) ||
                saved.Columns is null || saved.Columns.Length is < 1 or > 1999 ||
                saved.Columns.Any(c => c is null || c.Name is null || c.HeaderName is null || !Enum.IsDefined(c.Kind)) ||
                documents.Any(d => d.TableName == saved.TableName))
                throw new InvalidDataException("復旧用の表情報が不正です。");
            try
            {
                foreach (var source in new[] { saved.SourcePath, saved.WorkbookSourcePath })
                    if (source is not null && (!Path.IsPathFullyQualified(source) || Path.GetFullPath(source).Length == 0)) throw new ArgumentException("原本のパスが不正です。");
            }
            catch (ArgumentException ex) { throw new InvalidDataException("復旧用の原本パスが不正です。", ex); }
            command.CommandText = $"PRAGMA table_info({Quote(saved.TableName)})";
            var actual = new List<string>(); using (var reader = command.ExecuteReader()) while (reader.Read()) actual.Add(reader.GetString(1));
            if (!actual.SequenceEqual(new[] { "__id" }.Concat(saved.Columns.Select(c => c.Name)), StringComparer.Ordinal))
                throw new InvalidDataException("列構造と復旧情報が一致しないため復旧しません。残っている作業データは削除されていません。");
            EncodingDetector.Resolve(saved.EncodingName); CsvCodec.ValidateDelimiter(saved.Delimiter);
            var doc = new CsvDocument { TableName = saved.TableName, DisplayName = saved.DisplayName, SourcePath = saved.SourcePath,
                WorkbookId = saved.WorkbookId, WorksheetName = saved.WorksheetName, WorkbookSourcePath = saved.WorkbookSourcePath,
                EncodingName = saved.EncodingName, HasHeader = saved.HasHeader, Delimiter = saved.Delimiter, IsDirty = true };
            foreach (var col in saved.Columns) doc.Columns.Add(new CsvColumn(col.Name) { HeaderName = col.HeaderName, Kind = col.Kind });
            command.CommandText = $"SELECT COUNT(*) FROM {Quote(saved.TableName)}"; doc.RowCount = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            documents.Add(doc); nextTable = Math.Max(nextTable, int.Parse(saved.TableName.AsSpan(1), CultureInfo.InvariantCulture));
        }
        // Display maps and undo are transient. Rebuild them from recovered rows instead of trusting stale state.
        command.CommandText = "SELECT name,type FROM sqlite_master WHERE type IN ('table','trigger')";
        var stale = new List<(string Name, string Type)>();
        using (var reader = command.ExecuteReader()) while (reader.Read())
        {
            var name = reader.GetString(0); var type = reader.GetString(1);
            // A table may have been committed after the last metadata checkpoint. Keep it intact,
            // but reserve its number so creating a document cannot collide with an orphaned table.
            if (type == "table" && Regex.IsMatch(name, "^t[1-9][0-9]{0,8}$", RegexOptions.CultureInvariant))
                nextTable = Math.Max(nextTable, int.Parse(name.AsSpan(1), CultureInfo.InvariantCulture));
            if (Regex.IsMatch(name, "^__undo_(old|new)_[0-9a-f]{32}(_(update|delete|insert))?$", RegexOptions.CultureInvariant) ||
                Regex.IsMatch(name, "^map_t[0-9]+_(base|[0-9A-F]{20})$", RegexOptions.CultureInvariant) ||
                type == "table" && Regex.IsMatch(name, "^scope_[0-9a-f]{32}$", RegexOptions.CultureInvariant)) stale.Add((name, type));
        }
        using (var transaction = connection.BeginTransaction())
        {
            command.Transaction = transaction;
            foreach (var item in stale.OrderByDescending(x => x.Type == "trigger"))
            { command.CommandText = $"DROP {item.Type.ToUpperInvariant()} IF EXISTS {Quote(item.Name)}"; command.ExecuteNonQuery(); }
            transaction.Commit();
        }
        SaveRecoveryCheckpoint();
    }

    public static void DiscardRecovery(WorkspaceRecoveryCandidate candidate)
    {
        if (!IsRecoverySession(candidate.Path)) throw new InvalidDataException("安全に確認できない作業フォルダーは削除しません。");
        using var sessionLock = AcquireRecoveryLock(candidate.Path);
        if (Directory.EnumerateFileSystemEntries(candidate.Path, "*", SearchOption.AllDirectories).Any(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0))
            throw new IOException("リンクを含む作業フォルダーは自動で削除しません。場所を確認して手動で整理してください。");
        Directory.Delete(candidate.Path, true);
    }

    private static bool IsRecoverySession(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!Regex.IsMatch(Path.GetFileName(full), "^session-[0-9a-f]{32}$", RegexOptions.CultureInvariant) ||
                !Directory.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) return false;
            foreach (var file in new[] { RecoveryMarker, "session.lock", "workspace.db" })
            {
                var child = Path.Combine(full, file);
                if (!File.Exists(child) || (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) return false;
            }
            foreach (var file in new[] { "workspace.db-wal", "workspace.db-shm" })
            {
                var child = Path.Combine(full, file);
                if (File.Exists(child) && (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) return false;
            }
            var marker = Path.Combine(full, RecoveryMarker);
            return new FileInfo(marker).Length < 128 && File.ReadAllText(marker) == "WinCSVTool recovery 1\n" + Path.GetFileName(full) + "\n";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private static FileStream AcquireRecoveryLock(string path) => new(Path.Combine(path, "session.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.Delete);
    private static SqliteConnection OpenRecoveryDatabase(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(path, "workspace.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        try { connection.Open(); return connection; } catch { connection.Dispose(); throw; }
    }
    private void ReportCleanupFailure(Exception error) => cleanupFailures.Add(RootPath + "\n" + error.Message);
    private void DisposeRecovery()
    {
        try { File.Delete(Path.Combine(RootPath, RecoveryMarker)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ReportCleanupFailure(ex); }
        recoveryLock?.Dispose(); recoveryLock = null;
    }
}
