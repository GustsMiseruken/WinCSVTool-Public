using WinCSVTool.Core;

namespace WinCSVTool;

internal sealed class SqlForm : Form
{
    private readonly MainForm _owner;
    private readonly SqlEditor _sql = new() { Font = new Font("Consolas", 11) };
    private readonly SqlSchemaTree _schema = new() { TabStop = true };
    private readonly DataGridView _grid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, VirtualMode = true, BackgroundColor = Color.White, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None, RowHeadersVisible = false };
    private readonly ToolStrip _bar = new() { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden, AutoSize = false, Padding = new Padding(10, 6, 10, 6), Height = 48 };
    private readonly ToolStripButton _cancel = new("中断") { Enabled = false };
    private readonly ToolStripButton _showSchema = new("テーブル一覧") { CheckOnClick = true, Checked = true, ToolTipText = "テーブルと列の一覧を表示します。幅が狭いパネルでは一覧を自動的に隠します。" };
    private readonly ToolStripDropDownButton _layout = new("表示位置");
    private readonly Label _result = new() { Dock = DockStyle.Bottom, Height = 38, Padding = new Padding(14, 9, 5, 0), Text = "SELECT / WITH を入力して実行します。結果の表示は最大 1,000 行です。" };
    private readonly SplitContainer _editorSplit;
    private readonly SplitContainer _sides;
    private readonly Panel _schemaPanel;
    private readonly Panel _editorPanel;
    private readonly Label _schemaTitle;
    private readonly Label _editorTitle;
    private readonly Label _resultsTitle;
    private bool _running;
    private bool _hosted;
    private bool _adaptingLayout;
    private bool _editorSplitterGesture;
    private bool _schemaSplitterGesture;
    private double _editorRatio = .4;
    private int _preferredSchemaWidth = 260;
    private QueryResult? _data;
    private Font? _editorAppearanceFont;
    private Font? _gridAppearanceFont;
    private int _gridRowHeight = 30;
    internal bool IsRunning => _running;

    public SqlForm(MainForm owner)
    {
        _editorAppearanceFont = _sql.Font;
        _owner = owner; Text = "SQL分析 — WinCSVTool"; Width = 1150; Height = 760; MinimumSize = new Size(850, 550);
        Font = owner.Font; StartPosition = FormStartPosition.CenterParent; BackColor = Color.White; AutoScaleMode = AutoScaleMode.Dpi;
        void Button(string text, string icon, Func<Task> action)
        {
            var b = new ToolStripButton(text) { Image = UiTheme.CreateIcon(icon), ImageScaling = ToolStripItemImageScaling.None, Padding = new Padding(8, 3, 8, 3) };
            b.Click += async (_, _) => await action(); _bar.Items.Add(b);
        }
        Button("実行  Ctrl+Enter", "run", QueryAsync); Button("全結果をCSVに保存…", "save", ExportAsync); Button("新規CSVタブへ…", "table", CreateAsync);
        _bar.Items.Add(new ToolStripSeparator()); _bar.Items.Add(_cancel);
        _bar.Items.Add(new ToolStripSeparator()); _bar.Items.Add(_showSchema); _bar.Items.Add(_layout);
        foreach (var (text, layout) in new[] { ("別ウィンドウ", SqlPaneLayout.SeparateWindow), ("CSVの右側", SqlPaneLayout.Right), ("CSVの下側", SqlPaneLayout.Bottom), ("同じウィンドウのタブ", SqlPaneLayout.Tab) })
        {
            var item = new ToolStripMenuItem(text) { Tag = layout };
            item.Click += async (_, _) => await _owner.SetSqlLayoutAsync(layout);
            _layout.DropDownItems.Add(item);
        }
        _cancel.Image = UiTheme.CreateIcon("cancel"); _cancel.ImageScaling = ToolStripItemImageScaling.None;
        _cancel.Click += (_, _) => _owner.CancelWork();
        _editorSplit = new SplitContainer { Size = new Size(850, 650), Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 230, SplitterWidth = 6, Panel1MinSize = 80, Panel2MinSize = 80 };
        _editorPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 10, 14, 10) };
        _editorPanel.Controls.Add(_sql);
        _editorTitle = new Label { Text = "SQL クエリ", Dock = DockStyle.Top, Height = 34, Padding = new Padding(14, 8, 0, 0) };
        _editorSplit.Panel1.Controls.Add(_editorPanel); _editorSplit.Panel1.Controls.Add(_editorTitle);
        _resultsTitle = new Label { Text = "結果プレビュー", Dock = DockStyle.Top, Height = 34, Padding = new Padding(14, 8, 0, 0) };
        _editorSplit.Panel2.Controls.Add(_grid); _editorSplit.Panel2.Controls.Add(_resultsTitle);
        _sides = new SplitContainer { Size = new Size(1150, 650), Dock = DockStyle.Fill, SplitterDistance = 260, SplitterWidth = 6, Panel1MinSize = 120, Panel2MinSize = 240 };
        _schemaPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 10, 10, 10) };
        _schemaPanel.Controls.Add(_schema);
        _schemaTitle = new Label { Text = "テーブルと列 · ダブルクリックで挿入", AutoEllipsis = true, Dock = DockStyle.Top, Height = 34, Padding = new Padding(14, 8, 0, 0) };
        _sides.Panel1.Controls.Add(_schemaPanel); _sides.Panel1.Controls.Add(_schemaTitle); _sides.Panel2.Controls.Add(_editorSplit);
        Controls.Add(_sides); Controls.Add(_bar); Controls.Add(_result);
        UiTheme.ThemeForm(this); UiTheme.ApplyToolStrip(_bar);
        _sql.BorderStyle = BorderStyle.None; _schema.BorderStyle = BorderStyle.None;
        _schema.BackColor = _schemaPanel.BackColor = UiTheme.Header;
        _schemaTitle.BackColor = _editorTitle.BackColor = _resultsTitle.BackColor = _result.BackColor = UiTheme.Header;
        _result.ForeColor = UiTheme.Muted;
        _grid.BackgroundColor = UiTheme.Surface; _grid.BorderStyle = BorderStyle.None;
        _grid.EnableHeadersVisualStyles = false; _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        _grid.ColumnHeadersHeight = 34; _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = UiTheme.Header, ForeColor = UiTheme.Text, SelectionBackColor = UiTheme.Header, SelectionForeColor = UiTheme.Text, Padding = new Padding(10, 0, 6, 0), Font = Font };
        _grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = UiTheme.Surface, ForeColor = UiTheme.Text, SelectionBackColor = UiTheme.Selection, SelectionForeColor = UiTheme.Text, Padding = new Padding(8, 0, 5, 0), Font = Font, Alignment = DataGridViewContentAlignment.MiddleLeft, WrapMode = DataGridViewTriState.False };
        _grid.GridColor = UiTheme.Border; _grid.RowTemplate.Height = 30; _grid.CellBorderStyle = DataGridViewCellBorderStyle.Single;
        _grid.AllowUserToDeleteRows = false; _grid.AllowUserToResizeRows = false;
        _sql.KeyDown += async (_, e) =>
        {
            var shortcut = _owner.Settings.GetShortcut("sqlRun");
            if (shortcut != Keys.None && e.KeyData == shortcut) { e.SuppressKeyPress = true; await QueryAsync(); }
        };
        _schema.NodeMouseDoubleClick += (_, e) =>
        {
            if (e.Node?.Tag is string identifier) _sql.InsertIdentifier(identifier);
        };
        _schema.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && _schema.SelectedNode?.Tag is string identifier)
            { e.SuppressKeyPress = true; _sql.InsertIdentifier(identifier); }
        };
        _grid.CellValueNeeded += (_, e) =>
        {
            if (_data is { } data && e.RowIndex >= 0 && e.RowIndex < data.Rows.Count && e.ColumnIndex >= 0 && e.ColumnIndex < data.Columns.Count)
                e.Value = data.Rows[e.RowIndex][e.ColumnIndex];
        };
        _grid.RowHeightInfoNeeded += (_, e) => { e.Height = _gridRowHeight; e.MinimumHeight = 2; };
        FormClosing += (_, e) => { if (_running) { _owner.CancelWork(); e.Cancel = true; } };
        _showSchema.CheckedChanged += (_, _) => AdaptLayout();
        Resize += (_, _) => AdaptLayout();
        // Child docking can finish after the form's Resize event; use its final available height.
        _editorSplit.SizeChanged += (_, _) => AdaptLayout();
        _editorSplit.SplitterMoving += (_, _) => { if (!_adaptingLayout) _editorSplitterGesture = true; };
        _editorSplit.SplitterMoved += (_, _) =>
        {
            if (_editorSplitterGesture && !_adaptingLayout)
            {
                var available = _editorSplit.ClientSize.Height - _editorSplit.SplitterWidth;
                if (available > 0) _editorRatio = Math.Clamp(_editorSplit.SplitterDistance / (double)available, .1, .9);
            }
            _editorSplitterGesture = false;
        };
        _sides.SplitterMoving += (_, _) => { if (!_adaptingLayout) _schemaSplitterGesture = true; };
        _sides.SplitterMoved += (_, _) =>
        {
            if (_schemaSplitterGesture && !_adaptingLayout) _preferredSchemaWidth = _sides.SplitterDistance;
            _schemaSplitterGesture = false;
        };
        if (owner.Current is { } page) _sql.Text = $"SELECT *\r\nFROM {Workspace.Quote(page.Document.TableName)};";
        RefreshSchema();
        Shown += (_, _) => _sql.Focus();
    }

    internal void PrepareHost(bool hosted)
    {
        _hosted = hosted;
        MinimumSize = hosted ? Size.Empty : new Size(850, 550);
        TopLevel = !hosted;
        FormBorderStyle = hosted ? FormBorderStyle.None : FormBorderStyle.Sizable;
        Dock = hosted ? DockStyle.Fill : DockStyle.None;
        ShowInTaskbar = !hosted;
        AdaptLayout();
    }

    internal void ApplyAppearance(AppSettings settings)
    {
        UiTheme.ThemeForm(this);
        UiTheme.ApplyToolStrip(_bar);
        var editorFont = UiTheme.CreateEditorFont(settings);
        if (_sql.Font.Equals(editorFont)) editorFont.Dispose();
        else
        {
            var previous = _editorAppearanceFont;
            _sql.Font = _editorAppearanceFont = editorFont;
            previous?.Dispose();
        }
        _sql.BorderStyle = _schema.BorderStyle = BorderStyle.None;
        _sql.ApplyColors();
        _schema.BackColor = _schemaPanel.BackColor = UiTheme.Header;
        _schemaTitle.BackColor = _editorTitle.BackColor = _resultsTitle.BackColor = _result.BackColor = UiTheme.Header;
        _result.ForeColor = UiTheme.Muted;
        _grid.BackgroundColor = UiTheme.Surface;
        _grid.GridColor = UiTheme.Border;
        _grid.DefaultCellStyle.BackColor = UiTheme.Surface;
        _grid.DefaultCellStyle.ForeColor = UiTheme.Text;
        _grid.DefaultCellStyle.SelectionBackColor = UiTheme.Selection;
        _grid.DefaultCellStyle.SelectionForeColor = UiTheme.Text;
        var gridFont = UiTheme.CreateGridFont(settings);
        if (_grid.DefaultCellStyle.Font?.Equals(gridFont) == true) gridFont.Dispose();
        else
        {
            var previous = _gridAppearanceFont;
            _grid.DefaultCellStyle.Font = _gridAppearanceFont = gridFont;
            previous?.Dispose();
        }
        _grid.ColumnHeadersDefaultCellStyle.BackColor = UiTheme.Header;
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = UiTheme.Text;
        _grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = UiTheme.Header;
        _grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = UiTheme.Text;
        _grid.ColumnHeadersDefaultCellStyle.Font = Font;
        _gridRowHeight = Math.Max(26, (int)Math.Ceiling(_grid.DefaultCellStyle.Font.GetHeight() + 12));
        _grid.RowTemplate.Height = _gridRowHeight;
        if (_grid.RowCount > 0) _grid.UpdateRowHeightInfo(0, true);
        foreach (ToolStripMenuItem item in _layout.DropDownItems) item.Checked = item.Tag is SqlPaneLayout layout && layout == settings.SqlLayout;
        var runShortcut = settings.GetShortcut("sqlRun");
        _bar.Items[0].Text = runShortcut == Keys.None ? "実行" : "実行  " + ShortcutCatalog.Format(runShortcut);
        // Regenerate native line icons so their foreground follows the active theme.
        foreach (ToolStripItem item in _bar.Items)
        {
            var text = item.Text ?? "";
            var key = text.StartsWith("実行") ? "run" : text.StartsWith("全結果") ? "save" : text.StartsWith("新規CSV") ? "table" : ReferenceEquals(item, _cancel) ? "cancel" : null;
            if (key is null) continue;
            var previous = item.Image;
            item.Image = UiTheme.CreateIcon(key);
            previous?.Dispose();
        }
        AdaptLayout();
        Invalidate(true);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _editorAppearanceFont?.Dispose();
            _gridAppearanceFont?.Dispose();
        }
    }

    private void AdaptLayout()
    {
        if (IsDisposed || _sides is null || _adaptingLayout) return;
        _adaptingLayout = true;
        try
        {
            var compact = _hosted && ClientSize.Height < 400;
            var tiny = compact && ClientSize.Height < 250;
            var titleHeight = compact ? Math.Max(26, Font.Height) : 34;
            _bar.Height = compact ? Math.Max(40, Font.Height + 14) : 48;
            _bar.Padding = compact ? new Padding(10, 2, 10, 2) : new Padding(10, 6, 10, 6);
            _result.Height = compact ? Math.Max(26, Font.Height + 2) : 38;
            _result.Padding = compact ? new Padding(10, 2, 5, 0) : new Padding(14, 9, 5, 0);
            _editorTitle.Height = _resultsTitle.Height = _schemaTitle.Height = titleHeight;
            _editorTitle.Visible = _resultsTitle.Visible = !tiny;
            _editorTitle.Padding = _resultsTitle.Padding = _schemaTitle.Padding = new Padding(14, compact ? 0 : 8, 0, 0);
            _editorPanel.Padding = compact ? new Padding(10, 2, 10, 2) : new Padding(14, 10, 14, 10);
            _schemaPanel.Padding = compact ? new Padding(10, 4, 8, 4) : new Padding(14, 10, 10, 10);
            _grid.ColumnHeadersHeight = compact ? Math.Max(26, Font.Height + 2) : 34;
            _sides.Panel1Collapsed = !_showSchema.Checked || (_hosted && ClientSize.Width < 740);
            if (!_schemaSplitterGesture && !_sides.Panel1Collapsed && _sides.Width >= 366)
                _sides.SplitterDistance = Math.Clamp(_preferredSchemaWidth, 120, Math.Max(120, _sides.Width - _sides.SplitterWidth - 240));
            // Chrome heights changed above; finish docking before computing the proportional split.
            PerformLayout();
            _sides.PerformLayout();
            _sides.Panel2.PerformLayout();
            var available = Math.Max(0, _editorSplit.ClientSize.Height - _editorSplit.SplitterWidth);
            var editorMinimum = (tiny ? 0 : titleHeight) + _editorPanel.Padding.Vertical + _sql.Font.Height + SystemInformation.HorizontalScrollBarHeight;
            var resultsMinimum = (tiny ? 0 : titleHeight) + _grid.ColumnHeadersHeight + _gridRowHeight;
            var scale = Math.Min(1d, available / (double)Math.Max(1, editorMinimum + resultsMinimum));
            editorMinimum = (int)(editorMinimum * scale);
            resultsMinimum = (int)(resultsMinimum * scale);
            _editorSplit.Panel1MinSize = _editorSplit.Panel2MinSize = 0;
            _editorSplit.Panel1MinSize = editorMinimum;
            _editorSplit.Panel2MinSize = resultsMinimum;
            if (available > 0)
                _editorSplit.SplitterDistance = Math.Clamp((int)Math.Round(available * _editorRatio), editorMinimum, Math.Max(editorMinimum, available - resultsMinimum));
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
            TreeNode Identifier(string text, string identifier) => new(text)
            {
                Tag = Workspace.Quote(identifier), ToolTipText = $"ダブルクリックまたはEnterで {Workspace.Quote(identifier)} を挿入"
            };
            var database = Identifier("main — 作業データベース", "main");
            foreach (var document in _owner.Workspace.Documents)
            {
                var table = Identifier($"{document.TableName} — {document.DisplayName}", document.TableName);
                foreach (var column in document.Columns) table.Nodes.Add(Identifier(column.Name, column.Name));
                database.Nodes.Add(table);
                table.Expand();
            }
            _schema.Nodes.Add(database);
            database.Expand();
            var help = new TreeNode("SQL関数と書き方");
            help.Nodes.Add("数値: WCT_NUMBER(列)");
            help.Nodes.Add("日時: WCT_TIMESTAMP(列)");
            help.Nodes.Add("真偽: WCT_BOOLEAN(列)");
            help.Nodes.Add("正規表現: 列 REGEXP '式'");
            help.Nodes.Add("結合: FROM t1 a JOIN t2 b ON a.\"id\" = b.\"id\"");
            _schema.Nodes.Add(help);
            help.Expand();
        }
        finally { _schema.EndUpdate(); }
    }

    private async Task<bool> Run(string title, Action<CancellationToken> work)
    {
        if (_running) return false; _running = true; _sql.Enabled = false;
        foreach (ToolStripItem item in _bar.Items) item.Enabled = ReferenceEquals(item, _cancel);
        try { return await _owner.RunWork(title, work); }
        finally
        {
            _running = false; _sql.Enabled = true;
            foreach (ToolStripItem item in _bar.Items) item.Enabled = !ReferenceEquals(item, _cancel);
        }
    }

    private async Task QueryAsync()
    {
        var sql = _sql.Text; QueryResult? data = null;
        if (!await Run("SQLを実行", token => data = new AnalysisService(_owner.Workspace).Query(sql, 1000, token))) return;
        _grid.RowCount = 0; _data = null; _grid.Columns.Clear();
        for (var i = 0; i < data!.Columns.Count; i++) _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "c" + i, HeaderText = data.Columns[i], Width = 180, SortMode = DataGridViewColumnSortMode.NotSortable });
        _data = data; _grid.RowCount = data.Rows.Count; _grid.Invalidate();
        _result.Text = $"{data.Rows.Count:N0} 行を表示{(data.Truncated ? "（表示上限に達しました。全件はCSV保存または新規タブへ）" : "")}";
    }

    private async Task ExportAsync()
    {
        var defaults = new CsvDocument
        {
            TableName = "", DisplayName = "query.csv", EncodingName = _owner.Settings.DefaultSaveEncodingName,
            Delimiter = _owner.Settings.DefaultDelimiter, HasHeader = true
        };
        var options = Dialogs.Csv(this, true, defaults, _owner.Settings.DefaultWriteBom); if (options is null) return;
        using var dialog = new SaveFileDialog { Filter = "CSV|*.csv|すべてのファイル|*.*", FileName = "query.csv" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var sql = _sql.Text; var path = dialog.FileName; long count = 0;
        if (await Run("SQLの全結果をCSVに保存", token => count = new AnalysisService(_owner.Workspace).ExportQuery(sql, path, options, token))) _result.Text = $"{count:N0} 行を保存しました。";
    }

    private async Task CreateAsync()
    {
        var name = Dialogs.Text(this, "SQL結果から新規CSV", "新規タブ名", "query.csv"); if (name is null) return;
        var sql = _sql.Text; CsvDocument? doc = null;
        if (!await Run("SQL結果から新規CSVを作成", token => doc = new AnalysisService(_owner.Workspace).CreateFromQuery(sql, name, token))) return;
        await _owner.AddDocumentAsync(doc!); RefreshSchema(); _result.Text = $"{doc!.RowCount:N0}行の新規CSVタブを作成しました。";
    }
}
