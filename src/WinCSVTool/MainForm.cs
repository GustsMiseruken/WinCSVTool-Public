using WinCSVTool.Core;

namespace WinCSVTool;

internal sealed partial class MainForm : Form
{
    internal Workspace Workspace { get; }
    private readonly MenuStrip _menu = new();
    private readonly ToolStrip _tools = new() { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(12, 8, 12, 8), Height = 50, AutoSize = false };
    private readonly ModernTabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _state = new("準備完了 — CSVを開くか、ここにドラッグしてください") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripProgressBar _progress = new() { Style = ProgressBarStyle.Marquee, Visible = false };
    private readonly ToolStripButton _cancel = new("中断") { Enabled = false };
    private readonly ToolStripStatusLabel _fileInfo = new() { TextAlign = ContentAlignment.MiddleRight };
    private readonly TextBox _cellText = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, MaxLength = 0, BorderStyle = BorderStyle.None };
    private readonly Label _cellLabel = new() { Text = "セルの内容", Dock = DockStyle.Top, Height = 26, AutoEllipsis = true };
    private readonly Label _address = new() { Text = "—", Dock = DockStyle.Left, Width = 82, TextAlign = ContentAlignment.TopCenter, Padding = new Padding(0, 9, 0, 0) };
    private readonly Panel _inspector = new() { Dock = DockStyle.Bottom, Height = 126, Padding = new Padding(14, 9, 14, 10) };
    private SpreadsheetGrid? _emptyGrid;
    private ToolStripButton? _wrapButton;
    private CancellationTokenSource? _operation;
    private bool _operationCancellable;
    private bool _busy;
    private bool _wrap;
    private SqlForm? _sqlForm;
    internal DocumentPage? Current => _tabs.SelectedTab is WorkbookPage book ? book.Current : _tabs.SelectedTab as DocumentPage;

    public MainForm(Workspace workspace, string[] initialFiles, AppSettings? settings = null, SettingsStore? settingsStore = null)
    {
        Settings = settings?.Clone() ?? new AppSettings(); Settings.Normalize();
        _settingsStore = settingsStore ?? new SettingsStore(); UiTheme.SetTheme(Settings.Theme);
        Workspace = workspace; Text = "WinCSVTool " + Program.DisplayVersion; Width = 1340; Height = 880; MinimumSize = new Size(920, 650);
        StartPosition = FormStartPosition.CenterScreen; Font = new Font("Yu Gothic UI", 9.5f); BackColor = UiTheme.Bg; ForeColor = UiTheme.Text; AllowDrop = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        BuildMenus(); BuildTools();
        UiTheme.ApplyToolStrip(_menu); UiTheme.ApplyToolStrip(_tools); UiTheme.ApplyToolStrip(_status);
        _menu.Padding = new Padding(12, 5, 12, 5);
        _status.Padding = new Padding(12, 3, 12, 3); _status.SizingGrip = false;
        _status.BackColor = UiTheme.Bg; _fileInfo.ForeColor = UiTheme.Muted;
        _state.ForeColor = UiTheme.Muted;
        _cancel.Image = UiTheme.CreateIcon("close", UiTheme.Muted, 14);
        _cancel.Click += (_, _) => { if (_operationCancellable) { CancelWork(); _state.Text = "中断を要求しました…"; } };
        _status.Items.AddRange([_state, _fileInfo, _progress, _cancel]);
        var apply = _applyCellButton;
        UiTheme.StyleButton(apply, true);
        apply.Click += (_, _) => { if (Current is { } p && p.Grid.CurrentCell is { } c) { p.Grid.EndEdit(); p.SetValue(c.RowIndex, c.ColumnIndex, _cellText.Text); } };
        _cellText.BackColor = UiTheme.Surface; _cellText.ForeColor = UiTheme.Text;
        _cellLabel.ForeColor = UiTheme.Muted; _address.ForeColor = UiTheme.Accent;
        _address.Font = new Font("Consolas", 10);
        _inspector.BackColor = UiTheme.Bg;
        var inputFrame = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface, Padding = new Padding(10, 7, 10, 7) };
        inputFrame.Paint += (_, e) => { using var pen = new Pen(UiTheme.Border); e.Graphics.DrawRectangle(pen, 0, 0, inputFrame.Width - 1, inputFrame.Height - 1); };
        inputFrame.Controls.Add(_cellText);
        var applyFrame = new Panel { Dock = DockStyle.Right, Width = 142, Padding = new Padding(12, 0, 0, 0) }; applyFrame.Controls.Add(apply);
        _inspector.Controls.Add(inputFrame); _inspector.Controls.Add(applyFrame); _inspector.Controls.Add(_address); _inspector.Controls.Add(_cellLabel);
        _inspector.Paint += (_, e) => { using var pen = new Pen(UiTheme.Border); e.Graphics.DrawLine(pen, 0, 0, Width, 0); };
        _tabs.SelectedIndexChanged += (_, _) => UpdateInspector();
        _tabs.CloseRequested += async page => await CloseWorkspaceTabAsync(page);
        _tabs.MouseUp += async (_, e) => { if (e.Button == MouseButtons.Middle) { for (var i = 0; i < _tabs.TabCount; i++) if (_tabs.GetTabRect(i).Contains(e.Location)) { await CloseWorkspaceTabAsync(_tabs.TabPages[i]); break; } } };
        _contentSplit.Panel1.Controls.Add(_tabs);
        _contentSplit.Panel1.Controls.Add(_inspector);
        Controls.Add(_contentSplit); Controls.Add(_tools); Controls.Add(_menu); Controls.Add(_status); MainMenuStrip = _menu;
        ShowEmptySheet();
        InitializePreferences();
        DragEnter += (_, e) => e.Effect = !_busy && e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += async (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) await OpenFilesAsync(files); };
        Shown += async (_, _) =>
        {
            foreach (var restored in Workspace.Documents.ToArray()) await AddDocumentAsync(restored);
            if (initialFiles.Length > 0) await OpenFilesAsync(initialFiles);
        };
        FormClosing += (_, e) =>
        {
            if (_busy) { CancelWork(); e.Cancel = true; _state.Text = _operationCancellable ? "中断の完了後に閉じてください" : "表示の更新完了後に閉じてください"; return; }
            foreach (var p in DocumentPages) if (!DiscardAllowed(p.Document)) { e.Cancel = true; return; }
            SaveWindowPreferences();
            if (_sqlForm?.IsDisposed == false) _sqlForm.Close();
        };
    }

    private void BuildMenus()
    {
        var file = CreateMenu("ファイル(&F)");
        Item(file, "CSV / Excelを開く…", async () => await ChooseOpenAsync(), Keys.Control | Keys.O, "open");
        Item(file, "新規CSV…", async () => await NewAsync(), Keys.Control | Keys.N, "new");
        Item(file, "名前を付けて保存…", async () => await SaveAsync(false), Keys.Control | Keys.S, "save");
        Item(file, "表示結果を保存…", async () => await SaveAsync(true));
        Item(file, "複数の表をExcelブックに保存…", SaveWorkbookAsync);
        Item(file, "文字コードを変更して開き直す…", ReloadEncodingAsync);
        Item(file, "ファイルの情報…", ShowPropertiesAsync, Keys.Alt | Keys.Enter, "properties");
        Item(file, "タブを閉じる", async () => await CloseWorkspaceTabAsync(_tabs.SelectedTab), Keys.Control | Keys.W, "closeTab");
        Item(file, "終了", () => { Close(); return Task.CompletedTask; }, commandId: "exit");
        var edit = CreateMenu("編集(&E)");
        _undoMenu = Item(edit, "取り消す（履歴なし）", UndoAsync, Keys.Control | Keys.Z, "undo");
        UpdateUndoMenu();
        Item(edit, "検索と置換…", FindAsync, Keys.Control | Keys.F, "find");
        Item(edit, "URL / Base64 デコード…", DecodeAsync, commandId: "decode");
        Item(edit, "空の行を末尾に追加", AppendAsync, commandId: "appendRow");
        Item(edit, "選択行を削除…", DeleteRowsAsync, commandId: "deleteRows");
        Item(edit, "列を追加…", AddColumnAsync, commandId: "addColumn");
        Item(edit, "選択列の名前を変更…", RenameColumnAsync, commandId: "renameColumn");
        Item(edit, "選択列を削除…", DeleteColumnAsync, commandId: "deleteColumn");
        var data = CreateMenu("データ(&D)");
        Item(data, "ソートとフィルター…", ViewAsync, commandId: "sortFilter");
        Item(data, "ソートとフィルターを解除", async () => { if (Current is { } p) await p.SetViewAsync(new()); }, commandId: "clearView");
        Item(data, "選択列の型を指定…", TypeAsync);
        Item(data, "統計…", StatsAsync, commandId: "stats");
        Item(data, "SQL分析", () => { OpenSql(); return Task.CompletedTask; }, Keys.Control | Keys.Q, "sql");
        var logs = CreateMenu("ログ解析(&L)");
        Item(logs, "IP・ASNを解析…", () => { OpenLogAnalysis(); return Task.CompletedTask; });
        var view = CreateMenu("表示(&V)");
        Item(view, "セルの折り返し切替", () => { ToggleWrap(); return Task.CompletedTask; }, commandId: "wrap");
        Item(view, "再表示", async () => { if (Current is { } p) await p.ReloadAsync(); }, Keys.F5, "refresh");
        Item(view, "表示を拡大", () => { SetZoomPercent(Settings.ZoomPercent + 10); return Task.CompletedTask; }, Keys.Control | Keys.Oemplus, "zoomIn");
        Item(view, "表示を縮小", () => { SetZoomPercent(Settings.ZoomPercent - 10); return Task.CompletedTask; }, Keys.Control | Keys.OemMinus, "zoomOut");
        Item(view, "表示倍率を100%に戻す", () => { SetZoomPercent(100); return Task.CompletedTask; }, Keys.Control | Keys.D0, "zoomReset");
        _inspectorMenu = Item(view, "セル内容パネルを表示", () => { Settings.InspectorVisible = !Settings.InspectorVisible; ApplyAppearance(); TrySaveSettings(); return Task.CompletedTask; });
        var options = CreateMenu("オプション(&O)");
        Item(options, "環境設定…", ShowSettingsAsync, Keys.Control | Keys.Oemcomma, "settings");
        var help = CreateMenu("ヘルプ(&H)");
        Item(help, "説明書 (README)", () =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, "README.md");
            if (File.Exists(path)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            else MessageBox.Show(this, "README.md はソースのルート、またはポータブル版のexeと同じフォルダーにあります。", "説明書");
            return Task.CompletedTask;
        });
        Item(help, "バージョン情報", () => { MessageBox.Show(this, "WinCSVTool " + Program.DisplayVersion + "\n大容量CSVの編集と分析\nWindows x64 / ローカル処理\n作業用DB: " + Workspace.RootPath, "WinCSVTool"); return Task.CompletedTask; });
    }

    private ToolStripMenuItem CreateMenu(string text) { var item = new ToolStripMenuItem(text); _menu.Items.Add(item); return item; }
    private ToolStripMenuItem Item(ToolStripMenuItem menu, string text, Func<Task> action, Keys keys = Keys.None, string? commandId = null)
    {
        var item = new ToolStripMenuItem(text) { ShortcutKeys = keys }; item.Click += async (_, _) => await action(); menu.DropDownItems.Add(item);
        if (commandId is not null) _shortcutItems[commandId] = item;
        return item;
    }

    private void BuildTools()
    {
        ToolStripButton Button(string text, string icon, Func<Task> action, string shortcut = "")
        {
            var button = new ToolStripButton(text, UiTheme.CreateIcon(icon)) { Padding = new Padding(9, 4, 9, 4),
                Margin = new Padding(2, 0, 2, 0), ToolTipText = shortcut.Length == 0 ? text : $"{text}  ({shortcut})", AccessibleName = text };
            button.Click += async (_, _) => await action(); _tools.Items.Add(button); return button;
        }
        var open = Button("開く", "open", ChooseOpenAsync, "Ctrl+O"); open.ForeColor = UiTheme.Accent; open.BackColor = UiTheme.AccentSoft;
        Button("新規", "new", NewAsync, "Ctrl+N"); Button("保存", "save", async () => await SaveAsync(false), "Ctrl+S");
        _tools.Items.Add(new ToolStripSeparator()); Button("検索 / 置換", "search", FindAsync, "Ctrl+F"); Button("デコード", "decode", DecodeAsync); Button("ソート / フィルター", "filter", ViewAsync);
        _tools.Items.Add(new ToolStripSeparator()); Button("列の型", "type", TypeAsync); Button("統計", "stats", StatsAsync);
        Button("SQL分析", "sql", () => { OpenSql(); return Task.CompletedTask; }, "Ctrl+Q");
        Button("ログ解析", "search", () => { OpenLogAnalysis(); return Task.CompletedTask; });
        _wrapButton = Button("折り返し", "wrap", () => { ToggleWrap(); return Task.CompletedTask; });
        _wrapButton.Alignment = ToolStripItemAlignment.Right;
        _tools.BackColor = UiTheme.Surface;
    }

    private void ToggleWrap()
    {
        _wrap = !_wrap; if (_wrapButton is not null) _wrapButton.Checked = _wrap;
        _updatingAppearance = true;
        try { foreach (var p in DocumentPages) p.SetWrap(_wrap); }
        finally { _updatingAppearance = false; }
    }

    private void ShowEmptySheet()
    {
        if (_tabs.TabCount > 0) return;
        var page = new TabPage("CSVワークスペース") { BackColor = UiTheme.Surface, Padding = new Padding(0), Tag = false };
        _emptyGrid = new SpreadsheetGrid { Dock = DockStyle.Fill, ReadOnly = true, AccessibleName = "CSVを開く前の空の表" };
        page.Controls.Add(_emptyGrid);
        _tabs.TabPages.Add(page); _emptyGrid.ApplyAppearance(Settings); UpdateInspector();
    }


    internal async Task<bool> RunWork(string title, Action<CancellationToken> action, bool canCancel = true)
    {
        if (_busy) return false;
        _busy = true; _operation = new(); _operationCancellable = canCancel; _progress.Visible = true; _cancel.Enabled = canCancel;
        _menu.Enabled = _tools.Enabled = _tabs.Enabled = _inspector.Enabled = false; _state.Text = title + "…";
        try { await Task.Run(() => action(_operation.Token)); _state.Text = title + " — 完了"; NotifyDocumentChanged(); return true; }
        catch (OperationCanceledException) { _state.Text = "処理を中断しました"; return false; }
        catch (Exception ex) { ShowError(ex); return false; }
        finally { _operation.Dispose(); _operation = null; _operationCancellable = false; _busy = false; _progress.Visible = false; _cancel.Enabled = false; _menu.Enabled = _tools.Enabled = _tabs.Enabled = _inspector.Enabled = true; }
    }

    internal void ShowError(Exception? ex)
    {
        _state.Text = "処理できませんでした";
        MessageBox.Show(this, ex?.Message ?? "エラーが発生しました", "WinCSVTool", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    internal void CancelWork() { if (_operationCancellable) _operation?.Cancel(); }

    private async Task ChooseOpenAsync()
    {
        using var dialog = new OpenFileDialog { Filter = "CSV / Excel|*.csv;*.tsv;*.txt;*.xlsx|CSV / TSV|*.csv;*.tsv;*.txt|Excelブック|*.xlsx|すべてのファイル|*.*", Multiselect = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) await OpenFilesAsync(dialog.FileNames);
    }

    internal async Task OpenFilesAsync(string[] paths)
    {
        if (_busy) return;
        var options = DefaultReadOptions();
        if (Settings.ConfirmReadSettings)
        {
            if (paths.All(IsXlsx))
            {
                using var format = new WorkbookImportForm(Settings.DefaultHasHeader);
                if (format.ShowDialog(this) != DialogResult.OK) return;
                options = options with { HasHeader = format.HasHeader };
            }
            else { var selected = Dialogs.Csv(this, false, DefaultReadDocument()); if (selected is null) return; options = selected; }
        }
        foreach (var path in paths)
        {
            if (IsXlsx(path))
            {
                IReadOnlyList<CsvDocument>? sheets = null;
                var workbookProgress = new Progress<ImportProgress>(p => _state.Text = $"{Path.GetFileName(path)} — {p.Rows:N0} 行を読み込み");
                if (!await RunWork("Excelブックを読み込み", token => sheets = new XlsxService(Workspace).Import(path, options.HasHeader, token, workbookProgress))) break;
                foreach (var sheet in sheets!) await AddDocumentAsync(sheet);
                if (_tabs.SelectedTab is WorkbookPage opened && opened.Sheets.TabCount > 0) opened.Sheets.SelectedIndex = 0;
                continue;
            }
            CsvDocument? document = null;
            var progress = new Progress<ImportProgress>(p => _state.Text = $"{Path.GetFileName(path)} — {p.Rows:N0} 行 / {p.BytesRead / 1048576:N0} MiB / {p.TotalBytes / 1048576:N0} MiB");
            if (!await RunWork("CSVを読み込み", token => document = Workspace.Import(path, options, token, progress))) break;
            await AddDocumentAsync(document!);
        }
    }

    internal async Task AddDocumentAsync(CsvDocument document)
    {
        foreach (var p in _tabs.TabPages.Cast<TabPage>().Where(p => p.Tag is false).ToArray()) { _tabs.TabPages.Remove(p); p.Dispose(); }
        _emptyGrid = null;
        var page = new DocumentPage(this, document); AttachDocumentPage(page, true);
        page.ApplyAppearance(Settings); await page.ReloadAsync(0); page.SetWrap(_wrap); _sqlForm?.RefreshSchema(); _logAnalysis?.RefreshDocuments();
    }

    private async Task NewAsync()
    {
        var name = Dialogs.Text(this, "新規CSV", "タブ名", "新規.csv"); if (name is null) return;
        CsvDocument? doc = null; if (await RunWork("新規CSVを作成", _ => doc = Workspace.CreateDocument(name, ["Column1"]))) await AddDocumentAsync(doc!);
    }

    private async Task SaveAsync(bool filtered)
    {
        if (Current is not { } p) return; p.Grid.EndEdit();
        var filename = string.Concat(p.Document.DisplayName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (!filtered && p.Document.WorkbookSourcePath is { } bookPath) filename = Path.GetFileName(bookPath);
        if (IsXlsx(filename)) filename = Path.GetFileNameWithoutExtension(filename) + "-values.xlsx";
        using var dialog = new SaveFileDialog { Filter = "CSV|*.csv|Excelブック|*.xlsx|TSV|*.tsv|すべてのファイル|*.*", FileName = string.IsNullOrWhiteSpace(filename) ? "new.csv" : filename, FilterIndex = IsXlsx(filename) ? 2 : 1, DefaultExt = IsXlsx(filename) ? "xlsx" : "csv", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (IsXlsx(dialog.FileName))
        {
            var pages = !filtered && _tabs.SelectedTab is WorkbookPage book ? book.Pages.ToArray() : [p];
            await ExportWorkbookAsync(dialog.FileName, pages, p.Document.HasHeader, filtered ? p.View : null); return;
        }
        var format = p.Document.SourcePath is null ? DefaultSaveDocument() : p.Document;
        var options = Dialogs.Csv(this, true, format, Settings.DefaultWriteBom); if (options is null) return;
        if (await RunWork("CSVを保存", token => Workspace.Export(p.Document, dialog.FileName, options, filtered ? p.View : null, token)))
        {
            if (!filtered && !options.EscapeSpreadsheetFormulas) { p.Document.IsDirty = false; p.Document.SourcePath = dialog.FileName; p.Document.DisplayName = Path.GetFileName(dialog.FileName); p.Document.EncodingName = options.EncodingName; p.Document.HasHeader = options.HasHeader; p.Document.Delimiter = options.Delimiter; }
            p.UpdateTitle(); RebuildWorkbookGroups(); _logAnalysis?.RefreshDocuments(); UpdateInspector();
            Workspace.ClearUndo(); NotifyDocumentChanged();
        }
    }

    private static bool DiscardAllowed(CsvDocument doc) => !doc.IsDirty || MessageBox.Show($"「{doc.DisplayName}」の未保存の変更を破棄しますか？\n変更を残すにはキャンセルしてCSVへ保存してください。", "未保存の変更", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK;

    private async Task CloseTabAsync(DocumentPage? page)
    {
        if (page is null || _busy || !DiscardAllowed(page.Document)) return;
        if (await RunWork("タブを閉じる", _ => { Workspace.ClearUndo(); Workspace.CloseDocument(page.Document); }))
        {
            if (page.Parent is TabControl sheets && sheets.Parent is WorkbookPage book)
            {
                book.Detach(page); page.Dispose();
                if (sheets.TabCount == 0) { _tabs.TabPages.Remove(book); book.Dispose(); }
            }
            else { _tabs.TabPages.Remove(page); page.Dispose(); }
            ShowEmptySheet(); _sqlForm?.RefreshSchema(); _logAnalysis?.RefreshDocuments(); UpdateInspector();
        }
    }

    private async Task ReloadEncodingAsync()
    {
        if (Current is not { } p || p.Document.SourcePath is not { } path || !DiscardAllowed(p.Document)) return;
        if (IsXlsx(path)) { _state.Text = "Excelブックの文字コードはファイル形式で定義されています。開き直す場合はファイルを再度開いてください。"; return; }
        var options = Dialogs.Csv(this, false, p.Document); if (options is null) return;
        CsvDocument? newDoc = null;
        if (!await RunWork("文字コードを変更して読み込み", token => newDoc = Workspace.Import(path, options, token))) return;
        await RunWork("元のタブを閉じる", _ => { Workspace.ClearUndo(); Workspace.CloseDocument(p.Document); }); _tabs.TabPages.Remove(p); p.Dispose(); await AddDocumentAsync(newDoc!);
    }

    private async Task ViewAsync()
    {
        if (Current is not { } p) return;
        using var dialog = new ViewDialog(p.Document, p.View);
        if (dialog.ShowDialog(this) == DialogResult.OK) await p.SetViewAsync(dialog.Result);
    }

    private async Task TypeAsync()
    {
        if (Current is not { } p || p.Grid.CurrentCell is not { } cell) return;
        var col = p.Document.Columns[cell.ColumnIndex];
        var kind = Dialogs.Choice(this, "列の型", $"{col.HeaderName} — 元の文字列は保持されます", Enum.GetValues<DataKind>(), (int)col.Kind,
            k => k switch { DataKind.Auto => "自動 (Auto)", DataKind.Text => "文字列 (Text)", DataKind.Number => "数値 (Number)", DataKind.Timestamp => "日時 (Timestamp)", _ => "真偽値 (Boolean)" });
        if (kind is null || kind.Value == col.Kind || !ConfirmUnsupportedUndo("列の型の変更")) return;
        Workspace.ClearUndo(); var previous = col.Kind; col.Kind = kind.Value; p.Document.IsDirty = true;
        if (await RunWork("列の型を変更", _ => Workspace.InvalidateView(p.Document)))
        { p.BuildColumns(); if (!await p.ReloadAsync()) { col.Kind = previous; p.BuildColumns(); await p.ReloadAsync(); } }
    }

    private async Task AppendAsync()
    {
        if (Current is not { } p) return;
        if (await RunWork("行を追加", _ => { using var undo = Workspace.BeginUndo(p.Document, "行の追加"); Workspace.AppendRow(p.Document); undo.Complete(); })) await p.ReloadAsync(Math.Max(0, (p.Document.RowCount - 1) / p.PageSize * p.PageSize));
    }

    private async Task DeleteRowsAsync()
    {
        if (Current is not { } p) return; var ids = p.SelectedRowIds();
        if (ids.Length == 0 || MessageBox.Show(this, $"選択中の{ids.Length:N0}行を削除しますか？", "行の削除", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        if (await RunWork("行を削除", _ => { using var undo = Workspace.BeginUndo(p.Document, "行の削除"); Workspace.DeleteRows(p.Document, ids); undo.Complete(); })) await p.ReloadAsync();
    }

    private async Task AddColumnAsync()
    {
        if (Current is { } page) await InsertColumnAsync(page, page.Document.Columns.Count);
    }

    private async Task RenameColumnAsync()
    {
        if (Current is not { } page) return;
        var columns = page.Grid.SelectedColumnIndexes();
        if (columns.Length != 1) { ShowError(new InvalidOperationException("名前を変更する列を1つだけ選択してください。")); return; }
        await RenameColumnAsync(page, columns[0]);
    }

    private async Task DeleteColumnAsync()
    {
        if (Current is { } page) await DeleteColumnsAsync(page, page.Grid.SelectedColumnIndexes());
    }

    private bool CanChangeColumns(DocumentPage page) => !_busy && !IsDisposed && !page.IsDisposed && Workspace.Documents.Contains(page.Document);

    internal async Task InsertColumnAsync(DocumentPage page, int index)
    {
        if (!CanChangeColumns(page) || index < 0 || index > page.Document.Columns.Count) return;
        var name = Dialogs.Text(this, "列を挿入", "新しい列名");
        if (name is null || !ConfirmUnsupportedUndo("列の挿入")) return;
        var committed = false;
        await RunWork("列を挿入", token => { Workspace.InsertColumn(page.Document, index, name, token); committed = true; });
        if (committed)
            await RefreshColumnStructureAsync(page.Document, column => column >= index ? column + 1 : column, index);
    }

    internal async Task RenameColumnAsync(DocumentPage page, int column)
    {
        if (!CanChangeColumns(page) || column < 0 || column >= page.Document.Columns.Count) return;
        var name = Dialogs.Text(this, "列名を変更", "新しい列名", page.Document.Columns[column].HeaderName);
        if (name is null || name == page.Document.Columns[column].HeaderName || !ConfirmUnsupportedUndo("列名の変更")) return;
        var committed = false;
        await RunWork("列名を変更", token => { Workspace.RenameColumn(page.Document, column, name, token); committed = true; });
        if (committed)
            await RefreshColumnStructureAsync(page.Document, value => value, column);
    }

    internal async Task DeleteColumnsAsync(DocumentPage page, int[] columns)
    {
        if (!CanChangeColumns(page)) return;
        var selected = columns.Distinct().Order().ToArray();
        if (selected.Length == 0 || selected.Any(column => column < 0 || column >= page.Document.Columns.Count)) return;
        if (selected.Length >= page.Document.Columns.Count) { ShowError(new InvalidOperationException("最後の1列は残してください。")); return; }
        var names = string.Join("、", selected.Take(5).Select(column => "「" + page.Document.Columns[column].HeaderName + "」"));
        if (selected.Length > 5) names += $" ほか{selected.Length - 5}列";
        if (!ConfirmUnsupportedUndo($"選択した{selected.Length}列（{names}）の削除")) return;
        var committed = false;
        await RunWork("選択列を削除", token => { Workspace.DeleteColumns(page.Document, selected, token); committed = true; });
        if (committed)
            await RefreshColumnStructureAsync(page.Document, column => Array.BinarySearch(selected, column) >= 0 ? null : column - selected.Count(value => value < column), selected[0]);
    }

    private async Task RefreshColumnStructureAsync(CsvDocument document, Func<int, int?> remap, int focusColumn)
    {
        foreach (var page in DocumentPages.Where(page => page.Document == document))
        {
            var updated = new ViewSpec();
            foreach (var rule in page.View.Filters)
                if (remap(rule.Column) is { } column) updated.Filters.Add(rule with { Column = column });
            foreach (var rule in page.View.Sorts)
                if (remap(rule.Column) is { } column) updated.Sorts.Add(rule with { Column = column });
            page.View = updated;
            page.InvalidateDisplayedRows(); page.BuildColumns(); page.ApplyAppearance(Settings);
            if (await page.ReloadAsync(canCancel: false) && page.Grid.RowCount > 0)
                page.Grid.CurrentCell = page.Grid[Math.Clamp(focusColumn, 0, document.Columns.Count - 1), 0];
            page.SetWrap(_wrap);
        }
        _sqlForm?.RefreshSchema(); _logAnalysis?.RefreshDocuments(); UpdateInspector();
    }

    private Task FindAsync()
    {
        if (Current is { } p) { using var f = new FindForm(this, p); f.ShowDialog(this); } return Task.CompletedTask;
    }

    private async Task StatsAsync()
    {
        if (Current is not { } p || p.Document.Columns.Count == 0) return;
        var scope = Dialogs.Choice(this, "統計の対象", "選択範囲は選択した行 × 選択した列です", new[] { StatsScope.Selection, StatsScope.CurrentView },
            display: s => s == StatsScope.Selection ? "選択範囲" : "現在のフィルター結果の全行・全列");
        if (scope is null) return;
        var selection = p.Selection(); var columns = scope == StatsScope.Selection ? selection.Columns : Enumerable.Range(0, p.Document.Columns.Count).ToArray();
        var selectedIds = scope == StatsScope.Selection ? p.SelectedRowIds() : null;
        var results = new List<(string Column, StatisticsResult Result)>();
        if (!await RunWork("統計を計算", token =>
        {
            var analysis = new AnalysisService(Workspace);
            foreach (var col in columns) results.Add((p.Document.Columns[col].HeaderName, scope == StatsScope.Selection
                ? analysis.StatsForRows(p.Document, selectedIds!, col, token) : analysis.Stats(p.Document, p.View, col, token: token)));
        })) return;
        using var f = new Form { Text = "統計 — " + p.Document.DisplayName, Width = 850, Height = 650, StartPosition = FormStartPosition.CenterParent, Font = Font };
        var grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
        grid.Columns.Add("column", "列"); grid.Columns.Add("metric", "統計"); grid.Columns.Add("value", "値");
        foreach (var result in results) foreach (var item in result.Result.Items) grid.Rows.Add(result.Column, item.Key, item.Value);
        f.Controls.Add(grid); UiTheme.ThemeForm(f); f.ShowDialog(this);
    }

    private async Task DecodeAsync()
    {
        if (Current is not { } p || p.Grid.CurrentCell is null) return;
        using var dialog = new DecodeForm(p.CurrentValue);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var selection = p.Selection(); var ids = dialog.WholeView ? null : p.SelectedRowIds(); long count = 0;
        var kind = dialog.Kind; var encoding = dialog.EncodingName; var plus = dialog.PlusAsSpace;
        BulkChangePreview? preview = null;
        RowScope? scope = null;
        try
        {
            if (!await RunWork("デコードの変更予定を確認", token =>
            {
                scope = ids is null ? Workspace.CaptureScope(p.Document, p.View, token) : Workspace.CaptureScope(p.Document, ids, token);
                preview = Workspace.PreviewDecode(p.Document, scope, selection.Columns, kind, encoding, plus, token);
            })) return;
            if (!ConfirmBulkChange("デコード", preview!, string.Join(", ", selection.Columns.Select(c => p.Document.Columns[c].HeaderName)))) return;
            if (await RunWork("デコード", token => { using var undo = Workspace.BeginUndo(p.Document, "デコード"); count = new DecodeService(Workspace).Apply(p.Document, scope!, selection.Columns, kind, encoding, plus, token); undo.Complete(); }))
            { await p.ReloadAsync(); _state.Text = $"{count:N0}セルをデコードしました"; }
        }
        finally { scope?.Dispose(); }
    }

    private enum StatsScope { Selection, CurrentView }
    private void OpenSql() => ShowSql(Settings.SqlLayout);
    internal void UpdateInspector()
    {
        if (_updatingAppearance) return;
        _inspector.Visible = Settings.InspectorVisible && _tabs.SelectedTab != _sqlTab && _tabs.SelectedTab != _logTab;
        if (Current is { } p && p.Grid.CurrentCell is { } c && c.ColumnIndex < p.Document.Columns.Count)
        {
            _cellText.Text = p.CurrentValue; _address.Text = $"{SpreadsheetGrid.ColumnLabel(c.ColumnIndex)}{p.Offset + c.RowIndex + 1}";
            _address.Width = Math.Max(82, TextRenderer.MeasureText(_address.Text, _address.Font).Width + 20);
            _cellLabel.Text = $"セルの内容  ·  {p.Document.Columns[c.ColumnIndex].HeaderName}  ·  {p.CurrentValue.Length:N0}文字";
            _fileInfo.Text = $"{p.Document.RowCount:N0} 行 × {p.Document.Columns.Count:N0} 列    │    {p.Document.EncodingName.ToUpperInvariant()}    │    {p.Document.TableName}";
        }
        else
        {
            _cellText.Text = ""; _address.Text = "—"; _address.Width = 82; _cellLabel.Text = "セルの内容 — 選択したセルの全文を表示・編集";
            _fileInfo.Text = Current is { } empty ? $"{empty.Document.RowCount:N0} 行 × {empty.Document.Columns.Count:N0} 列    │    {empty.Document.EncodingName.ToUpperInvariant()}" : "CSV / TSV    │    ローカル処理";
        }
    }
}
