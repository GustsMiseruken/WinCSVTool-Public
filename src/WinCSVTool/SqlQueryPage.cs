using WinCSVTool.Core;

namespace WinCSVTool;

/// <summary>One query's input, bounded result preview, and presentation state.</summary>
internal sealed class SqlQueryPage : TabPage
{
    internal SqlEditor Editor { get; } = new() { Font = new Font("Consolas", 11) };
    internal DataGridView ResultsGrid { get; } = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, VirtualMode = true, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
        RowHeadersVisible = false, BorderStyle = BorderStyle.None, EnableHeadersVisualStyles = false,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing
    };
    internal Label ResultMessage { get; } = new() { Dock = DockStyle.Bottom, AutoEllipsis = true };
    internal QueryResult? Data { get; private set; }
    internal long PreviewCharacters { get; private set; }
    internal long LastTouched;
    internal bool HasUnsavedInput => Editor.Text != _initialSql;
    private readonly string _initialSql;
    private readonly SplitContainer _split;
    private readonly Panel _editorPanel;
    private readonly Label _editorTitle = new() { Text = "SQL クエリ", Dock = DockStyle.Top };
    private readonly Label _resultsTitle = new() { Text = "結果プレビュー", Dock = DockStyle.Top };
    private readonly ToolTip _messageTip = new();
    private Font? _editorFont;
    private Font? _gridFont;
    private int _rowHeight = 30;
    private bool _adapting;
    private bool _splitterGesture;
    private bool _compact;
    private bool _tiny;
    private double _editorRatio = .4;
    private string? _executedSql;
    private string _lastInputText;

    internal SqlQueryPage(string name, string sql)
    {
        Text = name; Padding = Padding.Empty; _initialSql = _lastInputText = sql; _editorFont = Editor.Font;
        _split = new SplitContainer { Dock = DockStyle.Fill, Size = new Size(850, 650), Orientation = Orientation.Horizontal, SplitterDistance = 230, SplitterWidth = 6, Panel1MinSize = 80, Panel2MinSize = 80 };
        _editorPanel = new Panel { Dock = DockStyle.Fill };
        _editorPanel.Controls.Add(Editor); _split.Panel1.Controls.Add(_editorPanel); _split.Panel1.Controls.Add(_editorTitle);
        _split.Panel2.Controls.Add(ResultsGrid); _split.Panel2.Controls.Add(_resultsTitle);
        Controls.Add(_split); Controls.Add(ResultMessage);
        ResultsGrid.CellValueNeeded += (_, e) =>
        {
            if (Data is { } data && e.RowIndex >= 0 && e.RowIndex < data.Rows.Count && e.ColumnIndex >= 0 && e.ColumnIndex < data.Columns.Count)
                e.Value = data.Rows[e.RowIndex][e.ColumnIndex];
        };
        ResultsGrid.RowHeightInfoNeeded += (_, e) => { e.Height = _rowHeight; e.MinimumHeight = 2; };
        ResultsGrid.DefaultCellStyle.Padding = new Padding(8, 0, 5, 0);
        ResultsGrid.ColumnHeadersDefaultCellStyle.Padding = new Padding(10, 0, 6, 0);
        Editor.Text = sql;
        Editor.TextChanged += (_, _) =>
        {
            var current = Editor.Text;
            if (current == _lastInputText) return;
            _lastInputText = current;
            if (Data is not null && current != _executedSql) SetMessage("入力を変更しました。結果は直前に実行したSQLのプレビューです。");
        };
        _split.SizeChanged += (_, _) => AdaptLayout(_compact, _tiny);
        _split.SplitterMoving += (_, _) => { if (!_adapting) _splitterGesture = true; };
        _split.SplitterMoved += (_, _) =>
        {
            var available = _split.ClientSize.Height - _split.SplitterWidth;
            if (_splitterGesture && !_adapting && available > 0) _editorRatio = Math.Clamp(_split.SplitterDistance / (double)available, .1, .9);
            _splitterGesture = false;
        };
        SetMessage("SELECT / WITH を入力して実行します。結果の表示は最大 1,000 行です。");
    }

    internal void SetMessage(string text)
    {
        ResultMessage.Text = text; _messageTip.SetToolTip(ResultMessage, text);
    }

    internal void ShowResult(string sql, QueryResult data)
    {
        ResultsGrid.RowCount = 0; Data = null; ResultsGrid.Columns.Clear();
        for (var i = 0; i < data.Columns.Count; i++)
            ResultsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "c" + i, HeaderText = data.Columns[i], Width = 180, SortMode = DataGridViewColumnSortMode.NotSortable });
        Data = data; _executedSql = sql;
        PreviewCharacters = data.Columns.Sum(value => (long)value.Length + 16) + data.Rows.Sum(row => row.Sum(value => (long)value.Length + 16) + 16);
        ResultsGrid.RowCount = data.Rows.Count; ResultsGrid.Invalidate();
        SetMessage($"{data.Rows.Count:N0} 行を表示{(data.Truncated ? "（表示上限に達しました。全件はCSV保存または新規CSVへ）" : "")}");
    }

    internal void ReleasePreview()
    {
        ResultsGrid.RowCount = 0; ResultsGrid.Columns.Clear(); Data = null; _executedSql = null; PreviewCharacters = 0;
        SetMessage("メモリ使用量を抑えるためプレビューを解放しました。SQLを再実行すると表示できます。");
    }

    internal void ApplyAppearance(AppSettings settings)
    {
        UiTheme.Apply(this);
        var editorFont = UiTheme.CreateEditorFont(settings);
        if (Editor.Font.Equals(editorFont)) editorFont.Dispose();
        else { var previous = _editorFont; Editor.Font = _editorFont = editorFont; previous?.Dispose(); }
        Editor.BorderStyle = BorderStyle.None; Editor.ApplyColors();
        _editorTitle.BackColor = _resultsTitle.BackColor = ResultMessage.BackColor = UiTheme.Header;
        ResultMessage.ForeColor = UiTheme.Muted;
        UiTheme.StyleGrid(ResultsGrid); ResultsGrid.GridColor = UiTheme.Border;
        var gridFont = UiTheme.CreateGridFont(settings);
        if (_gridFont?.Equals(gridFont) == true) gridFont.Dispose();
        else { var previous = _gridFont; ResultsGrid.DefaultCellStyle.Font = _gridFont = gridFont; previous?.Dispose(); }
        ResultsGrid.ColumnHeadersDefaultCellStyle.Font = Font;
        _rowHeight = Math.Max(26, (int)Math.Ceiling((_gridFont ?? Font).GetHeight() + 12));
        ResultsGrid.RowTemplate.Height = _rowHeight;
        if (ResultsGrid.RowCount > 0) ResultsGrid.UpdateRowHeightInfo(0, true);
        AdaptLayout(_compact, _tiny);
    }

    internal void AdaptLayout(bool compact, bool tiny)
    {
        _compact = compact; _tiny = tiny;
        if (_adapting || IsDisposed || _split is null) return;
        _adapting = true;
        try
        {
            var titleHeight = compact ? Math.Max(26, Font.Height) : 34;
            ResultMessage.Height = compact ? Math.Max(26, Font.Height + 2) : 38;
            ResultMessage.Padding = compact ? new Padding(10, 2, 5, 0) : new Padding(14, 9, 5, 0);
            _editorTitle.Height = _resultsTitle.Height = titleHeight;
            _editorTitle.Visible = _resultsTitle.Visible = !tiny;
            _editorTitle.Padding = _resultsTitle.Padding = new Padding(14, compact ? 0 : 8, 0, 0);
            _editorPanel.Padding = compact ? new Padding(10, 2, 10, 2) : new Padding(14, 10, 14, 10);
            ResultsGrid.ColumnHeadersHeight = compact ? Math.Max(26, Font.Height + 2) : 34;
            PerformLayout(); _split.PerformLayout();
            var available = Math.Max(0, _split.ClientSize.Height - _split.SplitterWidth);
            var editorMinimum = (tiny ? 0 : titleHeight) + _editorPanel.Padding.Vertical + Editor.Font.Height + SystemInformation.HorizontalScrollBarHeight;
            var resultsMinimum = (tiny ? 0 : titleHeight) + ResultsGrid.ColumnHeadersHeight + _rowHeight;
            var scale = Math.Min(1d, available / (double)Math.Max(1, editorMinimum + resultsMinimum));
            editorMinimum = (int)(editorMinimum * scale); resultsMinimum = (int)(resultsMinimum * scale);
            _split.Panel1MinSize = _split.Panel2MinSize = 0;
            _split.Panel1MinSize = editorMinimum; _split.Panel2MinSize = resultsMinimum;
            if (available > 0) _split.SplitterDistance = Math.Clamp((int)Math.Round(available * _editorRatio), editorMinimum, Math.Max(editorMinimum, available - resultsMinimum));
        }
        finally { _adapting = false; }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { Data = null; _messageTip.Dispose(); _editorFont?.Dispose(); _gridFont?.Dispose(); }
    }
}
