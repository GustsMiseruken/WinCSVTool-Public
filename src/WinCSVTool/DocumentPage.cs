using WinCSVTool.Core;

namespace WinCSVTool;

internal sealed partial class DocumentPage : TabPage
{
    internal int PageSize => Math.Clamp(_owner.Settings.PageSize, 100, 10000);
    private readonly MainForm _owner;
    private readonly ToolStripLabel _position = new() { ForeColor = UiTheme.Muted, Margin = new Padding(8, 0, 0, 0) };
    private readonly ToolStripTextBox _go = new() { Text = "1", Width = 84, BorderStyle = BorderStyle.FixedSingle, ToolTipText = "表示する行番号 (Enterで移動)" };
    private readonly ToolStripButton _first;
    private readonly ToolStripButton _previous;
    private readonly ToolStripButton _next;
    private readonly ToolStripButton _last;
    private List<CsvRow> _rows = [];
    private bool _displayInvalidated;
    public CsvDocument Document { get; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public ViewSpec View { get; set; } = new();
    public long Offset { get; private set; }
    public long Count { get; private set; }
    public SpreadsheetGrid Grid { get; } = new();

    public DocumentPage(MainForm owner, CsvDocument document)
    {
        _owner = owner; Document = document; Text = document.DisplayName; Padding = new Padding(0); BackColor = UiTheme.Surface;
        Grid.CellValueNeeded += (_, e) => { if (e.RowIndex >= 0 && e.RowIndex < _rows.Count && e.ColumnIndex >= 0 && e.ColumnIndex < _rows[e.RowIndex].Values.Length) e.Value = _rows[e.RowIndex].Values[e.ColumnIndex]; };
        Grid.CellValuePushed += (_, e) => { if (e.RowIndex >= 0 && e.RowIndex < _rows.Count && e.ColumnIndex >= 0) SetValue(e.RowIndex, e.ColumnIndex, e.Value?.ToString() ?? ""); };
        Grid.SelectionChanged += (_, _) => _owner.UpdateInspector();
        Grid.ColumnSortRequested += async (_, e) =>
        {
            var column = e.Column.Index; var old = View.Sorts.FirstOrDefault(); var candidate = new ViewSpec();
            candidate.Filters.AddRange(View.Filters); candidate.Sorts.Add(new SortRule(column, old?.Column == column && !old.Descending));
            await SetViewAsync(candidate);
        };
        Grid.DataError += (_, e) => { e.ThrowException = false; _owner.ShowError(e.Exception); };
        BuildColumnContextMenu();
        var pager = new ToolStrip { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(9, 5, 9, 5), AutoSize = false, Height = 40, CanOverflow = true };
        UiTheme.ApplyToolStrip(pager);
        _first = AddButton(pager, "|‹", "先頭のページ", async () => await ReloadAsync(0));
        _previous = AddButton(pager, "‹", "前のページ", async () => await ReloadAsync(Math.Max(0, Offset - PageSize)));
        _next = AddButton(pager, "›", "次のページ", async () => await ReloadAsync(Offset + PageSize));
        _last = AddButton(pager, "›|", "最後のページ", async () => await ReloadAsync(Math.Max(0, (Count - 1) / PageSize * PageSize)));
        foreach (var (button, icon) in new[] { (_first, "first"), (_previous, "previous"), (_next, "next"), (_last, "last") })
        { button.Image = UiTheme.CreateIcon(icon); button.DisplayStyle = ToolStripItemDisplayStyle.Image; }
        pager.Items.Add(new ToolStripSeparator { Margin = new Padding(6, 0, 6, 0) });
        pager.Items.Add(new ToolStripLabel("行へ移動") { ForeColor = UiTheme.Muted }); pager.Items.Add(_go);
        AddButton(pager, "移動", "指定した行を含むページへ", GoAsync);
        _go.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await GoAsync(); } };
        pager.Items.Add(_position);
        Controls.Add(Grid); Controls.Add(pager); BuildColumns(); ApplyAppearance(_owner.Settings);
    }

    private static ToolStripButton AddButton(ToolStrip bar, string text, string tooltip, Func<Task> action)
    {
        var b = new ToolStripButton(text) { ToolTipText = tooltip, Padding = new Padding(6, 2, 6, 2), Margin = new Padding(1, 0, 1, 0) };
        b.Click += async (_, _) => await action(); bar.Items.Add(b); return b;
    }

    private void BuildColumnContextMenu()
    {
        var menu = new ContextMenuStrip(); UiTheme.ApplyToolStrip(menu);
        var insertLeft = new ToolStripMenuItem("選択列の左に列を挿入…") { Name = "insertColumnLeft" };
        var insertRight = new ToolStripMenuItem("選択列の右に列を挿入…") { Name = "insertColumnRight" };
        var rename = new ToolStripMenuItem("列名を変更…") { Name = "renameColumn" };
        var delete = new ToolStripMenuItem("選択列を削除…") { Name = "deleteColumns" };
        var copy = new ToolStripMenuItem("コピー") { Name = "copyColumns" };
        insertLeft.Click += async (_, _) => { var columns = Grid.SelectedColumnIndexes(); if (columns.Length > 0) await _owner.InsertColumnAsync(this, columns.Min()); };
        insertRight.Click += async (_, _) => { var columns = Grid.SelectedColumnIndexes(); if (columns.Length > 0) await _owner.InsertColumnAsync(this, columns.Max() + 1); };
        rename.Click += async (_, _) => { var columns = Grid.SelectedColumnIndexes(); if (columns.Length == 1) await _owner.RenameColumnAsync(this, columns[0]); };
        delete.Click += async (_, _) => { var columns = Grid.SelectedColumnIndexes(); if (columns.Length > 0 && columns.Length < Document.Columns.Count) await _owner.DeleteColumnsAsync(this, columns); };
        copy.Click += (_, _) =>
        {
            try { if (Grid.GetClipboardContent() is { } content) Clipboard.SetDataObject(content, true); }
            catch (Exception error) { _owner.ShowError(error); }
        };
        menu.Items.AddRange([insertLeft, insertRight, rename, delete, new ToolStripSeparator(), copy]);
        menu.Opening += (_, e) =>
        {
            var columns = Grid.SelectedColumnIndexes();
            if (columns.Length == 0) { e.Cancel = true; return; }
            var canChange = !_displayInvalidated && !_owner.Workspace.HasIncompleteChange;
            insertLeft.Enabled = insertRight.Enabled = canChange;
            rename.Enabled = canChange && columns.Length == 1;
            delete.Enabled = canChange && columns.Length < Document.Columns.Count;
            copy.Enabled = !_displayInvalidated && Grid.SelectedCells.Count > 0;
            UiTheme.ApplyToolStrip(menu);
        };
        Grid.ContextMenuStrip = menu;
        Disposed += (_, _) => menu.Dispose();
    }

    private async Task GoAsync()
    {
        if (long.TryParse(_go.Text.Replace(",", ""), out var row) && row >= 1) await ReloadAsync((row - 1) / PageSize * PageSize);
    }

    public void BuildColumns()
    {
        Grid.RowCount = 0; Grid.Columns.Clear();
        foreach (var column in Document.Columns)
            Grid.Columns.Add(new DataGridViewTextBoxColumn { Name = column.Name, HeaderText = column.HeaderName, Width = 164, SortMode = DataGridViewColumnSortMode.Programmatic,
                MaxInputLength = 0, ToolTipText = $"SQL列: {column.Name}  /  型: {column.Kind}" });
    }

    public async Task<bool> ReloadAsync(long? offset = null, bool canCancel = true)
    {
        var count = 0L; var start = offset ?? Offset; var pageSize = PageSize; List<CsvRow>? rows = null;
        if (!await _owner.RunWork("表示データを読み込み", token =>
        {
            token.ThrowIfCancellationRequested(); count = _owner.Workspace.CountRows(Document, View, token);
            start = Math.Clamp(Math.Max(0, start) / pageSize * pageSize, 0, Math.Max(0, (count - 1) / pageSize * pageSize));
            token.ThrowIfCancellationRequested(); rows = _owner.Workspace.ReadPage(Document, View, start, pageSize, token);
        }, canCancel)) return false;
        if (IsDisposed || Disposing || _owner.IsDisposed || _owner.Disposing) return false;
        AcceptPage(count, start, rows!); return true;
    }

    private void AcceptPage(long count, long start, List<CsvRow> rows)
    {
        Count = count; Offset = start; _rows = rows;
        _displayInvalidated = false; Grid.ReadOnly = _owner.Workspace.HasIncompleteChange;
        Grid.RowNumberOffset = Offset; Grid.RowCount = _rows.Count;
        foreach (DataGridViewColumn column in Grid.Columns)
        {
            var sort = View.Sorts.FirstOrDefault(s => s.Column == column.Index);
            column.HeaderCell.SortGlyphDirection = sort is null ? SortOrder.None : sort.Descending ? SortOrder.Descending : SortOrder.Ascending;
        }
        Grid.Invalidate();
        _position.Text = $"{(Count == 0 ? 0 : Offset + 1):N0}–{Offset + _rows.Count:N0} / {Count:N0} 行{(View.IsEmpty ? "" : "  ·  ソート / フィルター適用中")}";
        _first.Enabled = _previous.Enabled = Offset > 0;
        _next.Enabled = _last.Enabled = Offset + _rows.Count < Count;
        _go.Text = (Offset + 1).ToString(); UpdateTitle(); _owner.UpdateInspector();
    }

    internal void InvalidateDisplayedRows()
    {
        // A failed/cancelled refresh must never leave old values available for another edit.
        Grid.CancelEdit(); Grid.ReadOnly = true; _displayInvalidated = true;
        _rows = []; Grid.ClearSelection(); Grid.CurrentCell = null; Grid.RowCount = 0;
        _position.Text = "表示の更新が必要です — 行へ移動で再読み込みできます";
        UpdateTitle(); Grid.Invalidate(); _owner.UpdateInspector();
    }

    public async Task SetViewAsync(ViewSpec candidate)
    {
        var previous = View; View = candidate;
        if (!await ReloadAsync(0)) View = previous;
    }

    public void UpdateTitle() => Text = (Document.WorksheetName ?? Document.DisplayName) + (Document.IsDirty ? " *" : "");
    public CsvRow? CurrentRow => Grid.CurrentCell is { RowIndex: >= 0 } c && c.RowIndex < _rows.Count ? _rows[c.RowIndex] : null;
    public string CurrentValue => Grid.CurrentCell is { ColumnIndex: >= 0 } c && CurrentRow is { } row && c.ColumnIndex < row.Values.Length ? row.Values[c.ColumnIndex] : "";
    public long[] SelectedRowIds() => Grid.SelectedCells.Cast<DataGridViewCell>().Select(c => c.RowIndex).Distinct().Where(i => i >= 0 && i < _rows.Count).Select(i => _rows[i].Id).ToArray();
    public (long Start, long End, int[] Columns) Selection()
    {
        var cells = Grid.SelectedCells.Cast<DataGridViewCell>().Where(c => c.RowIndex >= 0 && c.RowIndex < _rows.Count && c.ColumnIndex >= 0 && c.ColumnIndex < Document.Columns.Count).ToArray();
        return cells.Length == 0 ? (Offset, Offset, [0]) : (Offset + cells.Min(c => c.RowIndex), Offset + cells.Max(c => c.RowIndex), cells.Select(c => c.ColumnIndex).Distinct().Order().ToArray());
    }

    public void SetValue(int row, int col, string value)
    {
        if (_displayInvalidated || _owner.Workspace.HasIncompleteChange || row < 0 || row >= _rows.Count || col < 0 || col >= Document.Columns.Count || col >= _rows[row].Values.Length) return;
        if (_rows[row].Values[col] == value) return;
        try
        {
            using (var undo = _owner.Workspace.BeginUndo(Document, "セルの編集"))
            {
                _owner.Workspace.UpdateCell(Document, _rows[row].Id, col, value);
                undo.Complete();
            }
            // Keep the displayed row identities until an explicit page/view refresh.
            // Selection-based operations use these IDs, never re-evaluated offsets.
            _rows[row].Values[col] = value;
            UpdateTitle(); Grid.InvalidateCell(col, row);
            _owner.NotifyDocumentChanged();
        }
        catch (Exception ex) { _owner.ShowError(ex); }
    }

    public void SetWrap(bool wrap)
    {
        Grid.SetWrap(wrap);
    }

    internal void ApplyAppearance(AppSettings settings)
    {
        UiTheme.Apply(this);
        Grid.ApplyAppearance(settings);
        _position.ForeColor = UiTheme.Muted;
        foreach (var pager in Controls.OfType<ToolStrip>())
            foreach (var label in pager.Items.OfType<ToolStripLabel>()) label.ForeColor = UiTheme.Muted;
    }
}
