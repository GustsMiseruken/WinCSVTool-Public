using WinCSVTool.Core;

namespace WinCSVTool;

internal sealed partial class MainForm
{
    internal IEnumerable<DocumentPage> DocumentPages => _tabs.TabPages.Cast<TabPage>().SelectMany(page => page is WorkbookPage book ? book.Pages : page is DocumentPage document ? [document] : Enumerable.Empty<DocumentPage>());
    private static bool IsXlsx(string path) => Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase);

    private void AttachDocumentPage(DocumentPage page, bool select)
    {
        if (page.Document.WorkbookId is { } id)
        {
            var book = _tabs.TabPages.OfType<WorkbookPage>().FirstOrDefault(p => p.WorkbookId == id);
            if (book is null)
            {
                book = new WorkbookPage(this, id, Path.GetFileName(page.Document.WorkbookSourcePath ?? "Excelブック.xlsx"), CloseTabAsync);
                _tabs.TabPages.Add(book);
            }
            book.Add(page);
            if (select) { _tabs.SelectedTab = book; book.Sheets.SelectedTab = page; }
        }
        else { _tabs.TabPages.Add(page); if (select) _tabs.SelectedTab = page; }
    }

    private void RebuildWorkbookGroups()
    {
        var selected = Current?.Document; var pages = DocumentPages.ToArray();
        foreach (var page in pages)
            if (page.Parent is TabControl parent) { if (parent.Parent is WorkbookPage book) book.Detach(page); else parent.TabPages.Remove(page); }
        foreach (var book in _tabs.TabPages.OfType<WorkbookPage>().ToArray()) { _tabs.TabPages.Remove(book); book.Dispose(); }
        foreach (var page in pages) { page.UpdateTitle(); AttachDocumentPage(page, page.Document == selected); }
        UpdateInspector();
    }

    private async Task CloseWorkspaceTabAsync(TabPage? tab)
    {
        if (tab is null || _busy) return;
        if (tab == _sqlTab) { HideSql(); return; }
        if (tab == _logTab) { _tabs.TabPages.Remove(tab); tab.Dispose(); _logTab = null; _logAnalysis = null; ShowEmptySheet(); UpdateInspector(); return; }
        if (tab is WorkbookPage book)
        {
            var pages = book.Pages.ToArray();
            if (pages.Any(p => !DiscardAllowed(p.Document))) return;
            if (!await RunWork("Excelブックを閉じる", _ => { Workspace.ClearUndo(); foreach (var page in pages) Workspace.CloseDocument(page.Document); })) return;
            _tabs.TabPages.Remove(book); book.Dispose(); ShowEmptySheet(); _sqlForm?.RefreshSchema(); _logAnalysis?.RefreshDocuments(); UpdateInspector(); return;
        }
        await CloseTabAsync(tab as DocumentPage);
    }

    private async Task SaveWorkbookAsync()
    {
        if (_busy || !DocumentPages.Any()) return;
        using var options = new WorkbookSaveForm(DocumentPages.Select(p => p.Document).ToArray());
        if (options.ShowDialog(this) != DialogResult.OK) return;
        using var destination = new SaveFileDialog { Filter = "Excelブック|*.xlsx", DefaultExt = "xlsx", FileName = "workbook-values.xlsx", OverwritePrompt = true };
        if (destination.ShowDialog(this) != DialogResult.OK) return;
        var documents = options.SelectedDocuments;
        foreach (var page in DocumentPages.Where(p => documents.Contains(p.Document))) page.Grid.EndEdit();
        if (!ConfirmXlsxValueExport(destination.FileName, documents, false)) return;
        if (await RunWork("セル値をExcelブックへ出力", token => new XlsxService(Workspace).Export(destination.FileName, documents, options.IncludeHeader, token)))
        { Workspace.ClearUndo(); NotifyDocumentChanged(); RebuildWorkbookGroups(); _sqlForm?.RefreshSchema(); _logAnalysis?.RefreshDocuments(); }
    }

    private async Task ExportWorkbookAsync(string path, IReadOnlyList<DocumentPage> pages, bool header, ViewSpec? filtered)
    {
        if (!ConfirmXlsxValueExport(path, pages.Select(p => p.Document).ToArray(), filtered is not null)) return;
        if (await RunWork("セル値をExcelブックへ出力", token =>
        {
            if (filtered is null) new XlsxService(Workspace).Export(path, pages.Select(p => p.Document).ToArray(), header, token);
            else
            {
                using var scope = Workspace.CaptureScope(pages[0].Document, filtered, token);
                new XlsxService(Workspace).Export(path, [pages[0].Document], header, token, scope);
            }
        })) { Workspace.ClearUndo(); NotifyDocumentChanged(); RebuildWorkbookGroups(); _sqlForm?.RefreshSchema(); _logAnalysis?.RefreshDocuments(); }
    }

    private bool ConfirmXlsxValueExport(string path, IReadOnlyList<CsvDocument> documents, bool filtered)
    {
        var replacesSource = File.Exists(path) && documents.Any(d => SamePath(path, d.SourcePath) || SamePath(path, d.WorkbookSourcePath));
        var range = filtered ? "現在のシートのフィルター結果だけを1シートへ出力します。" : $"選択した {documents.Count:N0} シートの全行を出力します。各シートのフィルターは適用しません。";
        var warning = "これはセル値のエクスポートです。数式は計算済みの値になり、書式・グラフ・画像・マクロ等は保持されません。\n\n" + range + "\n\n";
        warning += replacesSource ? "元のExcelブックを上書きします。失われる情報を残すには、キャンセルして別名を指定してください。" : "元のブックを残すため、別名での保存を推奨します。";
        return MessageBox.Show(this, warning + "\n\n出力先: " + path + "\nセル値を出力しますか？", replacesSource ? "元のExcelブックの上書き — 情報が失われます" : "Excelへのセル値出力", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK;
    }

    private static bool SamePath(string first, string? second) => second is not null && string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
}

internal sealed class WorkbookImportForm : Form
{
    private readonly CheckBox _header = new() { AutoSize = true, Text = "各シートの先頭行をヘッダーとして扱う", Location = new Point(22, 23) };
    internal bool HasHeader => _header.Checked;
    internal WorkbookImportForm(bool header)
    {
        Text = "Excelブックの読み込み"; ClientSize = new Size(550, 210); StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; _header.Checked = header;
        var hint = new Label { AutoSize = false, Location = new Point(22, 65), Size = new Size(510, 70), Text = "全シートを読み込み、シートのタブは画面下側に表示します。\nセルの値を読み込みます。数式は保存済みの計算結果を使用します。" };
        var ok = new Button { Text = "開く", DialogResult = DialogResult.OK, Bounds = new Rectangle(298, 155, 108, 36) };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(416, 155, 112, 36) };
        Controls.AddRange([_header, hint, ok, cancel]); AcceptButton = ok; CancelButton = cancel; UiTheme.ThemeForm(this);
    }
}

internal sealed class WorkbookSaveForm : Form
{
    private readonly CheckedListBox _list = new() { Dock = DockStyle.Fill, CheckOnClick = true, BorderStyle = BorderStyle.None, IntegralHeight = false };
    private readonly CheckBox _header = new() { Text = "列名を各シートの先頭行に書き出す", AutoSize = true, Checked = true };
    private readonly IReadOnlyList<CsvDocument> _documents;
    internal IReadOnlyList<CsvDocument> SelectedDocuments => _list.CheckedIndices.Cast<int>().Select(i => _documents[i]).ToArray();
    internal bool IncludeHeader => _header.Checked;
    internal WorkbookSaveForm(IReadOnlyList<CsvDocument> documents)
    {
        _documents = documents; Text = "セル値をExcelブックにまとめる"; ClientSize = new Size(590, 440); MinimumSize = new Size(540, 400); StartPosition = FormStartPosition.CenterParent; Padding = new Padding(20);
        var heading = new Label { Dock = DockStyle.Top, Height = 88, Text = "表ごとに1シートを作成し、フィルターを適用せず全行を書き出します。\n数式・書式・グラフ等は保持せず、セル値だけを保存します。\n1シートの上限は、ヘッダーを含めて1,048,576行です。" };
        foreach (var document in documents) _list.Items.Add(document.WorkbookSourcePath is { } source ? Path.GetFileName(source) + " / " + document.WorksheetName : document.DisplayName, true);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 88, FlowDirection = FlowDirection.RightToLeft, WrapContents = true };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(116, 36) };
        var ok = new Button { Text = "保存先を選択…", AutoSize = true, MinimumSize = new Size(140, 36) };
        ok.Click += (_, _) => { if (_list.CheckedItems.Count == 0) return; DialogResult = DialogResult.OK; Close(); };
        footer.Controls.AddRange([cancel, ok, _header]); Controls.Add(_list); Controls.Add(heading); Controls.Add(footer); AcceptButton = ok; CancelButton = cancel; UiTheme.ThemeForm(this);
    }
}
