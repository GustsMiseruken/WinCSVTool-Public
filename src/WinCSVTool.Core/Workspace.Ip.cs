using System.Globalization;
using Microsoft.Data.Sqlite;

namespace WinCSVTool.Core;

[Flags]
public enum IpEnrichmentFields
{
    None = 0,
    Country = 1,
    CountryCode = 2,
    AddressClass = 4,
    Asn = 8,
    Organization = 16,
    SnapshotDate = 32,
    Source = 64,
    License = 128,
    All = 255
}

public sealed partial class Workspace
{
    /// <summary>Adds selected reference IP fields to every row in one transaction, without materializing the document in memory.</summary>
    public long EnrichIpAddresses(CsvDocument document, int column, IpAnalysisService service, string language = "ja",
        CancellationToken token = default, IProgress<ImportProgress>? progress = null, IpEnrichmentFields fields = IpEnrichmentFields.All)
    {
        if (fields == IpEnrichmentFields.None)
            throw new ArgumentException("追加するIP解析列を1つ以上選択してください。", nameof(fields));
        if ((fields & ~IpEnrichmentFields.All) != IpEnrichmentFields.None)
            throw new ArgumentException("追加するIP解析列に不明な種類が含まれています。", nameof(fields));
        ValidateColumn(document, column);
        ArgumentNullException.ThrowIfNull(service);
        var japanese = IpAnalysisService.IsJapanese(language);
        string[] headers = japanese ? ["国・地域（参考）", "国コード", "IP区分", "ASN", "AS組織・事業者（参考）", "IPデータ基準日", "IPデータ出典", "IPデータライセンス"]
            : ["Country (reference)", "Country code", "IP class", "ASN", "AS organization (reference)", "IP data snapshot", "IP data source", "IP data license"];
        var selected = Enumerable.Range(0, headers.Length).Where(i => (fields & (IpEnrichmentFields)(1 << i)) != IpEnrichmentFields.None).ToArray();
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!documents.Contains(document)) throw new InvalidOperationException("この作業領域の表ではありません。");
            if (pendingUndo is not null) throw new InvalidOperationException("編集履歴を記録中のため、列構造を変更できません。");
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var cancellation = ObserveCancellation(connection, token);
            // Clearing the previous journal shares the schema transaction so cancellation or failure
            // preserves both the original columns and their usable Undo history.
            DropUndoTables(connection, transaction, undoEntry);
            var newColumns = new List<CsvColumn>();
            var used = document.Columns.Select(c => c.Name).ToList();
            foreach (var index in selected)
            {
                var header = headers[index];
                var name = UniqueName(header, used); used.Add(name);
                using var add = connection.CreateCommand();
                add.Transaction = transaction;
                add.CommandText = $"ALTER TABLE {Quote(document.TableName)} ADD COLUMN {Quote(name)} TEXT NOT NULL DEFAULT ''";
                ExecuteCommand(add, token);
                newColumns.Add(new CsvColumn(name) { HeaderName = header, Kind = DataKind.Text });
            }
            // A bounded batch avoids updating the table while a SELECT cursor is open on it.
            const int batchSize = 512;
            using var select = connection.CreateCommand();
            select.Transaction = transaction;
            select.CommandText = $"SELECT __id,{Quote(document.Columns[column].Name)} FROM {Quote(document.TableName)} WHERE __id > $after ORDER BY __id LIMIT {batchSize}";
            select.Parameters.Add("$after", SqliteType.Integer);
            select.Prepare();
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = $"UPDATE {Quote(document.TableName)} SET {string.Join(',', newColumns.Select((c, i) => Quote(c.Name) + "=$v" + i))} WHERE __id=$id";
            for (var i = 0; i < newColumns.Count; i++) update.Parameters.Add("$v" + i, SqliteType.Text);
            update.Parameters.Add("$id", SqliteType.Integer);
            update.Prepare();
            long lastId = 0; long processed = 0;
            progress?.Report(new ImportProgress(0, 0, document.RowCount));
            while (true)
            {
                token.ThrowIfCancellationRequested();
                select.Parameters[0].Value = lastId;
                var batch = new List<(long Id, string Ip)>(batchSize);
                using (var reader = ReadCommand(select, token))
                    while (ReadNext(reader, token)) batch.Add((reader.GetInt64(0), reader.IsDBNull(1) ? "" : reader.GetString(1)));
                if (batch.Count == 0) break;
                foreach (var item in batch)
                {
                    token.ThrowIfCancellationRequested();
                    var result = service.Lookup(item.Ip, language);
                    string[] values = [result.CountryName, result.CountryCode, result.AddressClass, result.Asn?.ToString(CultureInfo.InvariantCulture) ?? "", result.Organization, result.SnapshotDate, result.Source, result.License];
                    for (var i = 0; i < selected.Length; i++) update.Parameters[i].Value = values[selected[i]];
                    update.Parameters[selected.Length].Value = item.Id;
                    ExecuteCommand(update, token);
                    processed++; lastId = item.Id;
                }
                if ((processed & 8191) == 0) progress?.Report(new ImportProgress(processed, processed, document.RowCount));
            }
            progress?.Report(new ImportProgress(processed, processed, document.RowCount));
            DropDocumentMaps(connection, transaction, document, token);
            WriteRecoveryCheckpoint(connection, transaction, document, newColumns);
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            document.Columns.AddRange(newColumns);
            document.IsDirty = true;
            maps.Remove(document.TableName);
            undoEntry = null;
            return processed;
        }
    }
}
