using WinCSVTool.Core;

namespace WinCSVTool;

internal sealed partial class MainForm
{
    private ToolStripMenuItem? _undoMenu;
    private bool _checkpointWarningShown;

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == Settings.GetShortcut("undo"))
        {
            Control focused = this;
            while (focused.Controls.Cast<Control>().FirstOrDefault(c => c.ContainsFocus) is { } child) focused = child;
            if (focused is TextBoxBase text)
            {
                if (text.CanUndo) text.Undo();
                return true;
            }
        }
        return base.ProcessCmdKey(ref message, keyData);
    }

    internal void NotifyDocumentChanged()
    {
        UpdateUndoMenu();
        try { Workspace.SaveRecoveryCheckpoint(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            _state.Text = "作業復旧情報を保存できませんでした。変更はファイルへ保存してください。";
            if (!_checkpointWarningShown)
            {
                _checkpointWarningShown = true;
                MessageBox.Show(this, "作業復旧情報を保存できませんでした。異常終了した場合に復旧できない可能性があります。\n変更はファイルへ保存してください。\n" + ex.Message,
                    "作業復旧情報", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    private void UpdateUndoMenu()
    {
        if (_undoMenu is null) return;
        _undoMenu.Enabled = Workspace.CanUndo;
        _undoMenu.Text = Workspace.CanUndo ? "取り消す — " + Workspace.UndoDescription : "取り消す（履歴なし）";
    }

    private async Task UndoAsync()
    {
        if (!Workspace.CanUndo || _busy) return;
        Current?.Grid.EndEdit(); CsvDocument? restored = null;
        if (!await RunWork("直前の変更を取り消す", token => restored = Workspace.Undo(token)) || restored is null) return;
        foreach (var page in DocumentPages.Where(p => p.Document == restored))
        { await page.ReloadAsync(); page.UpdateTitle(); }
        _sqlForm?.RefreshSchema(); _logAnalysis?.RefreshDocuments(); UpdateInspector(); UpdateUndoMenu();
    }

    internal bool ConfirmUnsupportedUndo(string operation)
    {
        return MessageBox.Show(this, operation + "は取り消しに対応していません。実行すると直前の取り消し履歴も消去します。\n元のファイルは保存するまで変更されません。続けますか？",
            "取り消しに対応していない操作", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK;
    }

    internal bool ConfirmBulkChange(string operation, BulkChangePreview preview, string columnNames)
    {
        if (preview.ChangedCells == 0) { _state.Text = "変更されるセルはありません。"; return false; }
        if (columnNames.Length > 240) columnNames = columnNames[..240] + "…";
        return MessageBox.Show(this, $"対象: {preview.Rows:N0} 行 × {preview.Columns:N0} 列\n列: {columnNames}\n予定変更: {preview.ChangedCells:N0} セル\n\n" +
            string.Join("\n", preview.Examples) + "\n\n直前の変更を1回取り消せます。取り消し用の作業ディスクを使用します。実行しますか？",
            operation + " — 実行前の確認", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) == DialogResult.OK;
    }
}
