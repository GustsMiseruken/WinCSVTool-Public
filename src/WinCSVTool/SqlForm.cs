using WinCSVTool.Core;

namespace WinCSVTool;

internal sealed class SqlForm : Form
{
    internal const int MaximumQueryTabs = 32;
    private const long PreviewCharacterBudget = 16 * 1024 * 1024;
    private readonly MainForm _owner;
    // Active-editor alias retained for the keyboard/insertion path and existing UI integrations.
    private SqlEditor _sql = null!;
    private readonly ModernTabControl _queryTabs = new() { Dock = DockStyle.Fill };
    private readonly SqlSchemaTree _schema = new() { TabStop = true };
    private readonly ToolStrip _bar = new() { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden, AutoSize = false, Padding = new Padding(10, 6, 10, 6), Height = 48 };
    private readonly ToolStripButton _cancel = new("中断") { Enabled = false };
    private readonly ToolStripButton _newQuery = new("＋ 新規クエリ");
    private readonly ToolStripDropDownButton _queries = new("クエリ");
    private readonly ToolStripMenuItem _renameQuery = new("名前を変更…");
    private readonly ToolStripMenuItem _closeQuery = new("クエリを閉じる");
    private readonly List<ToolStripButton> _commands = [];
    private readonly ToolStripButton _showSchema = new("テーブル一覧") { CheckOnClick = true, Checked = true, ToolTipText = "テーブルと列の一覧を表示します。幅が狭いパネルでは一覧を自動的に隠します。" };
    private readonly ToolStripDropDownButton _layout = new("表示位置");
    private readonly SplitContainer _sides;
    private readonly Panel _schemaPanel;
    private readonly Label _schemaTitle;
    private bool _running;
    private bool _hosted;
    private bool _adaptingLayout;
    private bool _schemaSplitterGesture;
    private int _preferredSchemaWidth = 260;
    private int _queryNumber;
    private long _clock;
    private SqlQueryPage? _runningPage;
    internal bool IsRunning => _running;
    internal SqlQueryPage? CurrentQuery => _queryTabs.SelectedTab as SqlQueryPage;

    public SqlForm(MainForm owner)
    {
        _owner = owner; Text = "SQL分析 — WinCSVTool"; Width = 1150; Height = 760; MinimumSize = new Size(850, 550);
        Font = owner.Font; StartPosition = FormStartPosition.CenterParent; BackColor = UiTheme.Surface; AutoScaleMode = AutoScaleMode.Dpi;
        void Button(string text, string icon, Func<Task> action)
        {
            var button = new ToolStripButton(text) { Image = UiTheme.CreateIcon(icon), ImageScaling = ToolStripItemImageScaling.None, Padding = new Padding(8, 3, 8, 3) };
            button.Click += async (_, _) => await action(); _bar.Items.Add(button); _commands.Add(button);
        }
        Button("実行  Ctrl+Enter", "run", QueryAsync); Button("全結果をCSVに保存…", "save", ExportAsync); Button("新規CSVタブへ…", "table", CreateAsync);
        _bar.Items.Add(new ToolStripSeparator()); _bar.Items.Add(_cancel);
        _bar.Items.Add(new ToolStripSeparator()); _bar.Items.Add(_newQuery); _bar.Items.Add(_queries);
        _queries.DropDownItems.AddRange([_renameQuery, _closeQuery]);
        _newQuery.Click += (_, _) => AddQuery()?.Editor.Focus();
        _renameQuery.Click += (_, _) => RenameQuery(); _closeQuery.Click += (_, _) => { if (CurrentQuery is { } page) CloseQuery(page); };
        _bar.Items.Add(new ToolStripSeparator()); _bar.Items.Add(_showSchema); _bar.Items.Add(_layout);
        foreach (var (text, layout) in new[] { ("別ウィンドウ", SqlPaneLayout.SeparateWindow), ("CSVの右側", SqlPaneLayout.Right), ("CSVの下側", SqlPaneLayout.Bottom), ("同じウィンドウのタブ", SqlPaneLayout.Tab) })
        {
            var item = new ToolStripMenuItem(text) { Tag = layout };
            item.Click += async (_, _) => await _owner.SetSqlLayoutAsync(layout); _layout.DropDownItems.Add(item);
        }
        _cancel.Image = UiTheme.CreateIcon("cancel"); _cancel.ImageScaling = ToolStripItemImageScaling.None; _cancel.Click += (_, _) => _owner.CancelWork();
        _sides = new SplitContainer { Size = new Size(1150, 650), Dock = DockStyle.Fill, SplitterDistance = 260, SplitterWidth = 6, Panel1MinSize = 120, Panel2MinSize = 240 };
        _schemaPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 10, 10, 10) }; _schemaPanel.Controls.Add(_schema);
        _schemaTitle = new Label { Text = "テーブルと列 · ダブルクリックで挿入", AutoEllipsis = true, Dock = DockStyle.Top, Height = 34, Padding = new Padding(14, 8, 0, 0) };
        _sides.Panel1.Controls.Add(_schemaPanel); _sides.Panel1.Controls.Add(_schemaTitle); _sides.Panel2.Controls.Add(_queryTabs);
        Controls.Add(_sides); Controls.Add(_bar);
        _queryTabs.SelectedIndexChanged += (_, _) =>
        {
            if (CurrentQuery is { } page) { _sql = page.Editor; page.LastTouched = ++_clock; }
            UpdateActions(); AdaptLayout();
        };
        _queryTabs.CloseRequested += page => { if (page is SqlQueryPage query) CloseQuery(query); };
        _queryTabs.MouseDoubleClick += (_, e) =>
        {
            for (var i = 0; i < _queryTabs.TabCount; i++)
                if (_queryTabs.GetTabRect(i).Contains(e.Location) && !_queryTabs.CloseBounds(i).Contains(e.Location)) { _queryTabs.SelectedIndex = i; RenameQuery(); break; }
        };
        _schema.NodeMouseDoubleClick += (_, e) => { if (e.Node?.Tag is string identifier) CurrentQuery?.Editor.InsertIdentifier(identifier); };
        _schema.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter && _schema.SelectedNode?.Tag is string identifier) { e.SuppressKeyPress = true; CurrentQuery?.Editor.InsertIdentifier(identifier); } };
        FormClosing += (_, e) => { if (_running) { _owner.CancelWork(); e.Cancel = true; } };
        _showSchema.CheckedChanged += (_, _) => AdaptLayout(); Resize += (_, _) => AdaptLayout();
        _sides.SplitterMoving += (_, _) => { if (!_adaptingLayout) _schemaSplitterGesture = true; };
        _sides.SplitterMoved += (_, _) => { if (_schemaSplitterGesture && !_adaptingLayout) _preferredSchemaWidth = _sides.SplitterDistance; _schemaSplitterGesture = false; };
        var initial = owner.Current is { } document ? $"SELECT *\r\nFROM {Workspace.Quote(document.Document.TableName)};" : "";
        AddQuery(initial); ApplyAppearance(owner.Settings); RefreshSchema();
        Shown += (_, _) => CurrentQuery?.Editor.Focus();
    }

    internal SqlQueryPage? AddQuery(string sql = "")
    {
        if (_queryTabs.TabCount >= MaximumQueryTabs) { CurrentQuery?.SetMessage($"クエリは最大 {MaximumQueryTabs} タブです。不要なタブを閉じてください。"); return null; }
        var page = new SqlQueryPage("SQL " + ++_queryNumber, sql);
        page.Editor.KeyDown += async (_, e) =>
        {
            var shortcut = _owner.Settings.GetShortcut("sqlRun");
            if (shortcut != Keys.None && e.KeyData == shortcut) { e.SuppressKeyPress = true; await QueryAsync(); }
        };
        _queryTabs.TabPages.Add(page); page.ApplyAppearance(_owner.Settings); _queryTabs.SelectedTab = page; _sql = page.Editor; page.LastTouched = ++_clock;
        UpdateActions(); AdaptLayout(); return page;
    }

    private void RenameQuery()
    {
        if (_running || CurrentQuery is not { } page) return;
        var name = Dialogs.Text(this, "クエリ名を変更", "クエリの名前", page.Text);
        if (!string.IsNullOrWhiteSpace(name) && !page.IsDisposed) page.Text = name.Trim();
    }

    internal bool CloseQuery(SqlQueryPage page)
    {
        if (ReferenceEquals(page, _runningPage) || !_queryTabs.TabPages.Contains(page)) return false;
        if (page.HasUnsavedInput && MessageBox.Show(this, $"「{page.Text}」のSQL入力と結果を破棄して閉じますか？", "クエリを閉じる", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return false;
        _queryTabs.TabPages.Remove(page); page.Dispose();
        if (_queryTabs.TabCount == 0 && !IsDisposed && !Disposing) AddQuery();
        UpdateActions(); return true;
    }

    private void UpdateActions()
    {
        if (IsDisposed) return;
        foreach (var button in _commands) button.Enabled = !_running && CurrentQuery is not null;
        _cancel.Enabled = _running; _newQuery.Enabled = _queryTabs.TabCount < MaximumQueryTabs;
        _renameQuery.Enabled = !_running && CurrentQuery is not null;
        _closeQuery.Enabled = CurrentQuery is { } current && !ReferenceEquals(current, _runningPage);
        _layout.Enabled = !_running;
    }

    internal void PrepareHost(bool hosted)
    {
        _hosted = hosted; MinimumSize = hosted ? Size.Empty : new Size(850, 550); TopLevel = !hosted;
        FormBorderStyle = hosted ? FormBorderStyle.None : FormBorderStyle.Sizable; Dock = hosted ? DockStyle.Fill : DockStyle.None; ShowInTaskbar = !hosted; AdaptLayout();
    }

    internal void ApplyAppearance(AppSettings settings)
    {
        UiTheme.ThemeForm(this); UiTheme.ApplyToolStrip(_bar);
        foreach (SqlQueryPage page in _queryTabs.TabPages) page.ApplyAppearance(settings);
        _schema.BorderStyle = BorderStyle.None; _schema.BackColor = _schemaPanel.BackColor = _schemaTitle.BackColor = UiTheme.Header;
        foreach (ToolStripMenuItem item in _layout.DropDownItems) item.Checked = item.Tag is SqlPaneLayout layout && layout == settings.SqlLayout;
        var shortcut = settings.GetShortcut("sqlRun"); _commands[0].Text = shortcut == Keys.None ? "実行" : "実行  " + ShortcutCatalog.Format(shortcut);
        foreach (var (item, icon) in new[] { (_commands[0], "run"), (_commands[1], "save"), (_commands[2], "table"), (_cancel, "cancel") })
        { var previous = item.Image; item.Image = UiTheme.CreateIcon(icon); previous?.Dispose(); }
        AdaptLayout(); UpdateActions(); Invalidate(true);
    }

    private void AdaptLayout()
    {
        if (IsDisposed || _sides is null || _adaptingLayout) return;
        _adaptingLayout = true;
        try
        {
            var compact = _hosted && ClientSize.Height < 400; var tiny = compact && ClientSize.Height < 250;
            _bar.Height = compact ? Math.Max(40, Font.Height + 14) : 48;
            _bar.Padding = compact ? new Padding(10, 2, 10, 2) : new Padding(10, 6, 10, 6);
            _schemaTitle.Height = compact ? Math.Max(26, Font.Height) : 34;
            _schemaTitle.Padding = new Padding(14, compact ? 0 : 8, 0, 0);
            _schemaPanel.Padding = compact ? new Padding(10, 4, 8, 4) : new Padding(14, 10, 10, 10);
            _sides.Panel1Collapsed = !_showSchema.Checked || (_hosted && ClientSize.Width < 740);
            if (!_schemaSplitterGesture && !_sides.Panel1Collapsed && _sides.Width >= 366)
                _sides.SplitterDistance = Math.Clamp(_preferredSchemaWidth, 120, Math.Max(120, _sides.Width - _sides.SplitterWidth - 240));
            PerformLayout(); _sides.PerformLayout(); _sides.Panel2.PerformLayout(); _queryTabs.PerformLayout();
            foreach (SqlQueryPage page in _queryTabs.TabPages) page.AdaptLayout(compact, tiny);
        }
        finally { _adaptingLayout = false; }
    }

    public void RefreshSchema()
    {
        if (IsDisposed) return;
        _schema.BeginUpdate();
        try
        {
            _schema.Nodes.Clear();
            TreeNode Identifier(string text, string identifier) => new(text) { Tag = Workspace.Quote(identifier), ToolTipText = $"ダブルクリックまたはEnterで {Workspace.Quote(identifier)} を挿入" };
            var database = Identifier("main — 作業データベース", "main");
            foreach (var document in _owner.Workspace.Documents)
            {
                var table = Identifier($"{document.TableName} — {document.DisplayName}", document.TableName);
                foreach (var column in document.Columns) table.Nodes.Add(Identifier(column.Name, column.Name));
                database.Nodes.Add(table); table.Expand();
            }
            _schema.Nodes.Add(database); database.Expand();
            var help = new TreeNode("SQL関数と書き方");
            help.Nodes.Add("数値: WCT_NUMBER(列)"); help.Nodes.Add("日時: WCT_TIMESTAMP(列)"); help.Nodes.Add("真偽: WCT_BOOLEAN(列)");
            help.Nodes.Add("正規表現: 列 REGEXP '式'"); help.Nodes.Add("結合: FROM t1 a JOIN t2 b ON a.\"id\" = b.\"id\"");
            _schema.Nodes.Add(help); help.Expand();
        }
        finally { _schema.EndUpdate(); }
    }

    private async Task<bool> Run(SqlQueryPage page, string title, Action<CancellationToken> work, Func<Task> completed)
    {
        if (_running || page.IsDisposed || !_queryTabs.TabPages.Contains(page)) return false;
        _running = true; _runningPage = page; page.Editor.Enabled = false; page.SetMessage(title + "…"); UpdateActions();
        Exception? failure = null;
        try
        {
            var success = await _owner.RunWork(title, token => { try { work(token); } catch (Exception exception) { failure = exception; throw; } });
            if (page.IsDisposed || IsDisposed || !_queryTabs.TabPages.Contains(page)) return false;
            if (success) await completed();
            else page.SetMessage((failure is OperationCanceledException ? "処理を中断しました。" : failure is not null ? "実行できませんでした。" : "処理を開始できませんでした。") + (page.Data is not null ? "直前の結果プレビューは保持しています。" : ""));
            return success;
        }
        finally
        {
            if (!page.IsDisposed) page.Editor.Enabled = true;
            _running = false; _runningPage = null; UpdateActions();
        }
    }

    private void TrimPreviews()
    {
        var pages = _queryTabs.TabPages.Cast<SqlQueryPage>().ToArray(); var total = pages.Sum(page => page.PreviewCharacters);
        foreach (var page in pages.Where(page => page != CurrentQuery && page != _runningPage && page.Data is not null).OrderBy(page => page.LastTouched))
        {
            if (total <= PreviewCharacterBudget) break;
            total -= page.PreviewCharacters; page.ReleasePreview();
        }
    }

    private async Task QueryAsync()
    {
        if (_running || CurrentQuery is not { } page) return;
        var sql = page.Editor.Text; QueryResult? data = null;
        await Run(page, "SQLを実行", token => data = new AnalysisService(_owner.Workspace).Query(sql, 1000, token), () =>
        { page.ShowResult(sql, data!); page.LastTouched = ++_clock; TrimPreviews(); return Task.CompletedTask; });
    }

    private async Task ExportAsync()
    {
        if (_running || CurrentQuery is not { } page) return;
        var sql = page.Editor.Text;
        var defaults = new CsvDocument { TableName = "", DisplayName = "query.csv", EncodingName = _owner.Settings.DefaultSaveEncodingName, Delimiter = _owner.Settings.DefaultDelimiter, HasHeader = true };
        var options = Dialogs.Csv(this, true, defaults, _owner.Settings.DefaultWriteBom); if (options is null) return;
        using var dialog = new SaveFileDialog { Filter = "CSV|*.csv|すべてのファイル|*.*", FileName = "query.csv" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await ExportQueryAsync(page, sql, dialog.FileName, options);
    }

    private Task<bool> ExportQueryAsync(SqlQueryPage page, string sql, string path, CsvOptions options)
    {
        long count = 0;
        return Run(page, "SQLの全結果をCSVに保存", token => count = new AnalysisService(_owner.Workspace).ExportQuery(sql, path, options, token), () => { page.SetMessage($"{count:N0} 行を保存しました。"); return Task.CompletedTask; });
    }

    private async Task CreateAsync()
    {
        if (_running || CurrentQuery is not { } page) return;
        var sql = page.Editor.Text;
        var name = Dialogs.Text(this, "SQL結果から新規CSV", "新規タブ名", "query.csv"); if (name is null) return;
        await CreateQueryAsync(page, sql, name);
    }

    private Task<bool> CreateQueryAsync(SqlQueryPage page, string sql, string name)
    {
        CsvDocument? document = null;
        return Run(page, "SQL結果から新規CSVを作成", token => document = new AnalysisService(_owner.Workspace).CreateFromQuery(sql, name, token), async () =>
        { await _owner.AddDocumentAsync(document!); RefreshSchema(); if (!page.IsDisposed) page.SetMessage($"{document!.RowCount:N0}行の新規CSVタブを作成しました。"); });
    }
}
