namespace WinCSVTool;

/// <summary>
/// A bounded, virtual CSV grid. Empty spreadsheet cells are painted only in the
/// visible viewport; they never become document rows, columns, or selectable data.
/// </summary>
internal sealed class SpreadsheetGrid : DataGridView
{
    private long _rowNumberOffset;
    private readonly Dictionary<DataGridViewColumn, float> _columnWidths = [];
    private float _zoomScale = 1;
    private bool _applyingAppearance;
    private bool _wrap;
    private Font? _appearanceFont;
    private int _headerSelectionAnchor = -1;
    private int _pressedHeaderColumn = -1;
    private bool _pressedLetterBand;
    private bool _headerResizeGesture;
    private bool _headerDragGesture;
    private Point _headerPressLocation;
    private bool _selectingHeaderColumns;
    private readonly HashSet<int> _emptySelectedColumns = [];

    internal event EventHandler<DataGridViewColumnEventArgs>? ColumnSortRequested;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public long RowNumberOffset
    {
        get => _rowNumberOffset;
        set { _rowNumberOffset = value; UpdateRowHeaderMetrics(); Invalidate(); }
    }

    public SpreadsheetGrid()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Fill;
        VirtualMode = true;
        AllowUserToAddRows = false;
        AllowUserToDeleteRows = false;
        AllowUserToResizeRows = false;
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        SelectionMode = DataGridViewSelectionMode.CellSelect;
        MultiSelect = true;
        BackgroundColor = UiTheme.Surface;
        BorderStyle = BorderStyle.None;
        CellBorderStyle = DataGridViewCellBorderStyle.Single;
        ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        RowHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        RowHeadersWidthSizeMode = DataGridViewRowHeadersWidthSizeMode.DisableResizing;
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        RowHeadersWidth = 76;
        ColumnHeadersHeight = 48;
        RowTemplate.Height = 28;
        EnableHeadersVisualStyles = false;
        GridColor = UiTheme.GridLine;
        DefaultCellStyle.BackColor = UiTheme.Surface;
        DefaultCellStyle.ForeColor = UiTheme.Text;
        DefaultCellStyle.SelectionBackColor = UiTheme.Selection;
        DefaultCellStyle.SelectionForeColor = UiTheme.Text;
        DefaultCellStyle.Padding = new Padding(8, 2, 8, 2);
        ColumnHeadersDefaultCellStyle.BackColor = UiTheme.Header;
        ColumnHeadersDefaultCellStyle.ForeColor = UiTheme.Text;
        RowHeadersDefaultCellStyle.BackColor = UiTheme.Header;
        RowHeadersDefaultCellStyle.ForeColor = UiTheme.Muted;
        ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText;
    }

    /// <summary>Updates visible worksheet metrics without changing the CSV or its selection.</summary>
    internal void ApplyAppearance(AppSettings settings)
    {
        SuspendLayout();
        _applyingAppearance = true;
        try
        {
            _zoomScale = Math.Clamp(settings.ZoomPercent, 50, 200) / 100f;
            var candidate = UiTheme.CreateGridFont(settings);
            if (_appearanceFont is { } existing && existing.Equals(candidate))
            {
                // WinForms keeps the old instance for value-equal fonts. Retain
                // that instance so its control and cell-style references stay valid.
                candidate.Dispose();
            }
            else
            {
                var oldFont = _appearanceFont;
                _appearanceFont = candidate;
                Font = candidate;
                DefaultCellStyle.Font = candidate;
                ColumnHeadersDefaultCellStyle.Font = candidate;
                RowHeadersDefaultCellStyle.Font = candidate;
                oldFont?.Dispose();
            }
            UiTheme.StyleGrid(this);
            DefaultCellStyle.Padding = new Padding(ScaleMetric(8), ScaleMetric(2), ScaleMetric(8), ScaleMetric(2));
            ApplyColumnWidths();
            ApplyRowHeight();
            UpdateHeaderMetrics();
        }
        finally { _applyingAppearance = false; ResumeLayout(); }
        Invalidate();
    }

    internal void SetWrap(bool wrap)
    {
        _wrap = wrap;
        DefaultCellStyle.WrapMode = wrap ? DataGridViewTriState.True : DataGridViewTriState.False;
        ApplyRowHeight();
        Invalidate();
    }

    private int ScaleMetric(float value) => Math.Max(1, (int)Math.Round(value * _zoomScale * DeviceDpi / 96f));

    private void ApplyColumnWidths()
    {
        foreach (DataGridViewColumn column in Columns)
            if (_columnWidths.TryGetValue(column, out var width)) column.Width = Math.Max(column.MinimumWidth, ScaleMetric(width));
    }

    private void ApplyRowHeight()
    {
        var height = Math.Max(ScaleMetric(_wrap ? 72 : 28), Font.Height + ScaleMetric(8));
        if (RowTemplate.Height == height) return;
        if (IsCurrentCellInEditMode && !EndEdit()) return;
        RowTemplate.Height = height;
        if (RowCount == 0) return;

        // Recreate the bounded page as shared virtual rows. Assigning individual
        // row heights would unshare every row and consume unnecessary memory.
        var count = RowCount;
        var current = CurrentCell is { } cell ? (cell.RowIndex, cell.ColumnIndex) : (-1, -1);
        var selected = SelectedCells.Cast<DataGridViewCell>().Select(c => (c.RowIndex, c.ColumnIndex)).ToArray();
        var firstRow = FirstDisplayedScrollingRowIndex;
        var horizontalOffset = HorizontalScrollingOffset;
        RowCount = 0; RowCount = count;
        if (current.Item1 >= 0 && current.Item1 < count && current.Item2 >= 0 && current.Item2 < ColumnCount)
            CurrentCell = this[current.Item2, current.Item1];
        ClearSelection();
        foreach (var (row, column) in selected)
            if (row >= 0 && row < count && column >= 0 && column < ColumnCount) this[column, row].Selected = true;
        if (firstRow >= 0 && firstRow < count) FirstDisplayedScrollingRowIndex = firstRow;
        HorizontalScrollingOffset = horizontalOffset;
    }

    internal static string ColumnLabel(int index)
    {
        if (index < 0) return "";
        var value = (long)index + 1;
        Span<char> label = stackalloc char[8];
        var position = label.Length;
        while (value > 0)
        {
            label[--position] = (char)('A' + (value - 1) % 26);
            value = (value - 1) / 26;
        }
        return new string(label[position..]);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UpdateHeaderMetrics();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        _applyingAppearance = true;
        try { ApplyColumnWidths(); ApplyRowHeight(); }
        finally { _applyingAppearance = false; }
        UpdateHeaderMetrics();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        if (IsHandleCreated) UpdateHeaderMetrics();
    }

    private void UpdateHeaderMetrics()
    {
        ColumnHeadersHeight = Math.Max(ScaleMetric(44), Font.Height * 2 + ScaleMetric(8));
        UpdateRowHeaderMetrics();
        Invalidate();
    }

    private void UpdateRowHeaderMetrics()
    {
        var largest = (RowNumberOffset + Math.Max(20, RowCount)).ToString("N0");
        RowHeadersWidth = Math.Max(ScaleMetric(76), TextRenderer.MeasureText(largest, Font, Size.Empty, TextFormatFlags.NoPadding).Width + ScaleMetric(20));
    }

    protected override void OnCellPainting(DataGridViewCellPaintingEventArgs e)
    {
        base.OnCellPainting(e);
        if (e.Handled) return;
        if (e.RowIndex == -1 && e.ColumnIndex >= 0)
        {
            PaintColumnHeader(e.Graphics!, e.CellBounds, e.ColumnIndex, Columns[e.ColumnIndex].HeaderText,
                CurrentCell?.ColumnIndex == e.ColumnIndex || (RowCount == 0 && _emptySelectedColumns.Contains(e.ColumnIndex)), Columns[e.ColumnIndex].HeaderCell.SortGlyphDirection);
            e.Handled = true;
        }
        else if (e.ColumnIndex == -1 && e.RowIndex >= 0)
        {
            PaintRowHeader(e.Graphics!, e.CellBounds, RowNumberOffset + e.RowIndex + 1,
                CurrentCell?.RowIndex == e.RowIndex);
            e.Handled = true;
        }
        else if (e.ColumnIndex == -1 && e.RowIndex == -1)
        {
            PaintCorner(e.Graphics!, e.CellBounds);
            e.Handled = true;
        }
        else if (e.ColumnIndex >= 0 && e.RowIndex >= 0)
        {
            e.Paint(e.ClipBounds, e.PaintParts & ~DataGridViewPaintParts.Focus);
            e.Handled = true;
        }
    }

    protected override void OnCurrentCellChanged(EventArgs e)
    {
        base.OnCurrentCellChanged(e);
        Invalidate();
    }

    protected override void OnColumnWidthChanged(DataGridViewColumnEventArgs e)
    {
        base.OnColumnWidthChanged(e);
        if (!_applyingAppearance && _columnWidths.ContainsKey(e.Column))
            _columnWidths[e.Column] = e.Column.Width / (_zoomScale * DeviceDpi / 96f);
        Invalidate();
    }

    protected override void OnColumnAdded(DataGridViewColumnEventArgs e)
    {
        base.OnColumnAdded(e);
        _emptySelectedColumns.Clear(); _headerSelectionAnchor = -1;
        _columnWidths[e.Column] = e.Column.Width;
        _applyingAppearance = true;
        try { e.Column.Width = Math.Max(e.Column.MinimumWidth, ScaleMetric(e.Column.Width)); }
        finally { _applyingAppearance = false; }
    }

    protected override void OnColumnRemoved(DataGridViewColumnEventArgs e)
    {
        _columnWidths.Remove(e.Column);
        _headerSelectionAnchor = -1; _emptySelectedColumns.Clear();
        base.OnColumnRemoved(e);
    }

    private static (Rectangle Letter, Rectangle Title) HeaderBands(Rectangle bounds)
    {
        var middle = bounds.Top + bounds.Height / 2;
        return (Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, middle),
            Rectangle.FromLTRB(bounds.Left, middle, bounds.Right, bounds.Bottom));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        _pressedHeaderColumn = -1; _headerResizeGesture = _headerDragGesture = false;
        var hit = HitTest(e.X, e.Y);
        if (e.Button == MouseButtons.Right && hit.ColumnIndex >= 0 &&
            hit.Type is DataGridViewHitTestType.ColumnHeader or DataGridViewHitTestType.Cell)
        {
            if (!EndEdit()) return;
            Focus();
            if (!SelectedColumnIndexes().Contains(hit.ColumnIndex)) SelectDisplayedColumns(hit.ColumnIndex, Keys.None);
            // Preserve a selected multi-column range, including when its clicked
            // cell is outside the selected rows. Native right-click would collapse it.
            return;
        }
        if (e.Button == MouseButtons.Left && hit.Type == DataGridViewHitTestType.ColumnHeader && hit.ColumnIndex >= 0)
        {
            var bounds = GetCellDisplayRectangle(hit.ColumnIndex, -1, false);
            _pressedHeaderColumn = hit.ColumnIndex; _headerPressLocation = e.Location;
            _pressedLetterBand = HeaderBands(bounds).Letter.Contains(e.Location);
            var grip = ScaleMetric(4);
            _headerResizeGesture = AllowUserToResizeColumns &&
                (Math.Abs(e.X - bounds.Right) <= grip || Math.Abs(e.X - bounds.Left) <= grip);
        }
        else if (hit.RowIndex >= 0 && hit.ColumnIndex >= 0) _headerSelectionAnchor = hit.ColumnIndex;
        base.OnMouseDown(e);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x007B && message.LParam.ToInt64() != -1 && ContextMenuStrip is not null)
        {
            var packed = message.LParam.ToInt64();
            var point = PointToClient(new Point(unchecked((short)packed), unchecked((short)(packed >> 16))));
            var hit = HitTest(point.X, point.Y);
            if (hit.ColumnIndex < 0 || hit.Type is not (DataGridViewHitTestType.ColumnHeader or DataGridViewHitTestType.Cell)) return;
        }
        base.WndProc(ref message);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_pressedHeaderColumn >= 0 && e.Button == MouseButtons.Left &&
            (Math.Abs(e.X - _headerPressLocation.X) > SystemInformation.DragSize.Width / 2 ||
             Math.Abs(e.Y - _headerPressLocation.Y) > SystemInformation.DragSize.Height / 2)) _headerDragGesture = true;
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        try { base.OnMouseUp(e); }
        finally { _pressedHeaderColumn = -1; _headerResizeGesture = _headerDragGesture = false; }
    }

    protected override void OnColumnHeaderMouseClick(DataGridViewCellMouseEventArgs e)
    {
        base.OnColumnHeaderMouseClick(e);
        if (e.Button != MouseButtons.Left || e.ColumnIndex < 0 || e.ColumnIndex != _pressedHeaderColumn ||
            _headerResizeGesture || _headerDragGesture) return;
        var bounds = GetCellDisplayRectangle(e.ColumnIndex, -1, false);
        var letterBand = e.Y < HeaderBands(bounds).Letter.Height;
        if (letterBand != _pressedLetterBand) return;
        if (letterBand) SelectDisplayedColumns(e.ColumnIndex, ModifierKeys);
        else ColumnSortRequested?.Invoke(this, new DataGridViewColumnEventArgs(Columns[e.ColumnIndex]));
    }

    internal int[] SelectedColumnIndexes() => RowCount == 0
        ? _emptySelectedColumns.Order().ToArray()
        : SelectedCells.Cast<DataGridViewCell>().Select(cell => cell.ColumnIndex).Distinct().Order().ToArray();

    private void SelectDisplayedColumns(int column, Keys modifiers)
    {
        if (!EndEdit()) return;
        var control = (modifiers & Keys.Control) != 0;
        var shift = (modifiers & Keys.Shift) != 0;
        var anchor = shift && _headerSelectionAnchor >= 0 && _headerSelectionAnchor < ColumnCount
            ? Columns[_headerSelectionAnchor].DisplayIndex : Columns[column].DisplayIndex;
        var target = Columns[column].DisplayIndex;
        var columns = Columns.Cast<DataGridViewColumn>().Where(c => c.Visible && c.DisplayIndex >= Math.Min(anchor, target) && c.DisplayIndex <= Math.Max(anchor, target)).Select(c => c.Index).ToArray();
        var select = !control || shift || (RowCount == 0 ? !_emptySelectedColumns.Contains(column) : Enumerable.Range(0, RowCount).Any(row => !this[column, row].Selected));
        _selectingHeaderColumns = true;
        try
        {
            if (RowCount > 0 && (!control || CurrentCell is null)) CurrentCell = this[column, 0];
            if (!control) { ClearSelection(); _emptySelectedColumns.Clear(); }
            foreach (var selectedColumn in columns)
            {
                if (RowCount == 0) { if (select) _emptySelectedColumns.Add(selectedColumn); else _emptySelectedColumns.Remove(selectedColumn); }
                for (var row = 0; row < RowCount; row++) this[selectedColumn, row].Selected = select;
            }
            if (!shift) _headerSelectionAnchor = column;
        }
        finally { _selectingHeaderColumns = false; base.OnSelectionChanged(EventArgs.Empty); Invalidate(); }
    }

    protected override void OnSelectionChanged(EventArgs e)
    {
        if (RowCount > 0) _emptySelectedColumns.Clear();
        if (!_selectingHeaderColumns) base.OnSelectionChanged(e);
    }

    protected override void OnScroll(ScrollEventArgs e)
    {
        base.OnScroll(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        PaintEmptyCells(e.Graphics);
        if (CurrentCell is not { RowIndex: >= 0, ColumnIndex: >= 0 } cell) return;
        var bounds = GetCellDisplayRectangle(cell.ColumnIndex, cell.RowIndex, false);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var clip = e.Graphics.Save();
        e.Graphics.SetClip(DataViewport());
        using var pen = new Pen(UiTheme.Accent, Math.Max(2, DeviceDpi / 48f));
        bounds.Width -= 1; bounds.Height -= 1;
        e.Graphics.DrawRectangle(pen, bounds);
        e.Graphics.Restore(clip);
    }

    private Rectangle DataViewport()
    {
        var width = ClientSize.Width;
        var height = ClientSize.Height;
        foreach (Control control in Controls)
        {
            if (!control.Visible) continue;
            if (control is VScrollBar) width = Math.Min(width, control.Left);
            else if (control is HScrollBar) height = Math.Min(height, control.Top);
        }
        var left = RowHeadersVisible ? RowHeadersWidth : 0;
        var top = ColumnHeadersVisible ? ColumnHeadersHeight : 0;
        return Rectangle.FromLTRB(left, top, Math.Max(left, width), Math.Max(top, height));
    }

    private void PaintEmptyCells(Graphics graphics)
    {
        var viewport = DataViewport();
        if (viewport.Width <= 0 || viewport.Height <= 0) return;
        var state = graphics.Save();
        graphics.SetClip(new Rectangle(0, 0, viewport.Right, viewport.Bottom));
        var visibleColumns = new List<Rectangle>();
        var rightStart = viewport.Left;
        var blankWidth = Math.Max(ScaleMetric(60), ScaleMetric(148));
        var lastColumnOrder = -1;
        foreach (DataGridViewColumn column in Columns)
        {
            if (!column.Visible) continue;
            if (column.DisplayIndex > lastColumnOrder)
            { lastColumnOrder = column.DisplayIndex; blankWidth = column.Width; }
            var rect = GetColumnDisplayRectangle(column.Index, false);
            if (rect.Width <= 0 || rect.Right <= viewport.Left || rect.Left >= viewport.Right) continue;
            visibleColumns.Add(rect);
            rightStart = Math.Max(rightStart, rect.Right);
        }
        rightStart = Math.Min(rightStart, viewport.Right);

        var visibleRows = new List<Rectangle>();
        var bottomStart = viewport.Top;
        var nextRow = RowNumberOffset + 1;
        var firstRow = Rows.GetFirstRow(DataGridViewElementStates.Displayed);
        for (var index = firstRow; index >= 0; index = Rows.GetNextRow(index, DataGridViewElementStates.Displayed))
        {
            var rect = GetRowDisplayRectangle(index, false);
            if (rect.Height <= 0 || rect.Bottom <= viewport.Top || rect.Top >= viewport.Bottom) continue;
            visibleRows.Add(rect);
            bottomStart = Math.Max(bottomStart, rect.Bottom);
            nextRow = RowNumberOffset + index + 2;
        }
        bottomStart = Math.Min(bottomStart, viewport.Bottom);
        using var surface = new SolidBrush(UiTheme.Surface);
        using var header = new SolidBrush(UiTheme.Header);
        using var line = new Pen(UiTheme.GridLine);

        // Paint only outside the actual data rectangle. The work is bounded by
        // visible cells even when the underlying CSV has millions of rows.
        graphics.FillRectangle(surface, Rectangle.FromLTRB(rightStart, viewport.Top, viewport.Right, viewport.Bottom));
        graphics.FillRectangle(surface, Rectangle.FromLTRB(viewport.Left, bottomStart, viewport.Right, viewport.Bottom));
        if (ColumnHeadersVisible)
            graphics.FillRectangle(header, Rectangle.FromLTRB(rightStart, 0, viewport.Right, viewport.Top));
        if (RowHeadersVisible)
            graphics.FillRectangle(header, Rectangle.FromLTRB(0, bottomStart, viewport.Left, viewport.Bottom));

        var blankHeight = Math.Max(1, RowTemplate.Height);
        var nextColumn = Columns.Count;
        for (var x = rightStart; x < viewport.Right; x += blankWidth)
        {
            var bounds = new Rectangle(x, 0, blankWidth, viewport.Top);
            if (ColumnHeadersVisible) PaintColumnHeader(graphics, bounds, nextColumn++, "", false, SortOrder.None);
            graphics.DrawLine(line, x + blankWidth - 1, viewport.Top, x + blankWidth - 1, viewport.Bottom);
        }
        foreach (var column in visibleColumns)
            graphics.DrawLine(line, column.Right - 1, bottomStart, column.Right - 1, viewport.Bottom);
        foreach (var row in visibleRows)
            graphics.DrawLine(line, rightStart, row.Bottom - 1, viewport.Right, row.Bottom - 1);

        for (var y = bottomStart; y < viewport.Bottom; y += blankHeight)
        {
            if (RowHeadersVisible) PaintRowHeader(graphics, new Rectangle(0, y, viewport.Left, blankHeight), nextRow++, false);
            graphics.DrawLine(line, viewport.Left, y + blankHeight - 1, viewport.Right, y + blankHeight - 1);
        }
        if (RowHeadersVisible && ColumnHeadersVisible && ColumnCount == 0) PaintCorner(graphics, new Rectangle(0, 0, viewport.Left, viewport.Top));
        graphics.Restore(state);
    }

    private void PaintColumnHeader(Graphics graphics, Rectangle bounds, int index, string title, bool active, SortOrder sort)
    {
        using var brush = new SolidBrush(active ? UiTheme.AccentSoft : UiTheme.Header);
        using var line = new Pen(UiTheme.Border);
        graphics.FillRectangle(brush, bounds);
        graphics.DrawLine(line, bounds.Right - 1, bounds.Top, bounds.Right - 1, bounds.Bottom);
        graphics.DrawLine(line, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);
        var color = active ? UiTheme.Accent : UiTheme.Muted;
        var bands = HeaderBands(bounds);
        var letter = Rectangle.Inflate(bands.Letter, -5, -2);
        TextRenderer.DrawText(graphics, ColumnLabel(index), Font, letter, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        if (!string.IsNullOrEmpty(title))
        {
            var text = new Rectangle(bounds.Left + 9, bands.Title.Top, bounds.Width - 18 - (sort == SortOrder.None ? 0 : 14), bands.Title.Height - 2);
            TextRenderer.DrawText(graphics, title, Font, text, active ? UiTheme.Accent : UiTheme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }
        if (sort != SortOrder.None)
        {
            TextRenderer.DrawText(graphics, sort == SortOrder.Ascending ? "↑" : "↓", Font,
                new Rectangle(bounds.Right - 22, bands.Title.Top, 18, bands.Title.Height), UiTheme.Accent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        if (active)
        {
            using var accent = new Pen(UiTheme.Accent, 2);
            graphics.DrawLine(accent, bounds.Left, bounds.Bottom - 1, bounds.Right - 1, bounds.Bottom - 1);
        }
    }

    private void PaintRowHeader(Graphics graphics, Rectangle bounds, long number, bool active)
    {
        using var brush = new SolidBrush(active ? UiTheme.AccentSoft : UiTheme.Header);
        using var line = new Pen(UiTheme.Border);
        graphics.FillRectangle(brush, bounds);
        graphics.DrawLine(line, bounds.Right - 1, bounds.Top, bounds.Right - 1, bounds.Bottom);
        graphics.DrawLine(line, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);
        TextRenderer.DrawText(graphics, number.ToString("N0"), Font, Rectangle.Inflate(bounds, -9, 0), active ? UiTheme.Accent : UiTheme.Muted,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        if (active)
        {
            using var accent = new Pen(UiTheme.Accent, 2);
            graphics.DrawLine(accent, bounds.Right - 1, bounds.Top, bounds.Right - 1, bounds.Bottom - 1);
        }
    }

    private static void PaintCorner(Graphics graphics, Rectangle bounds)
    {
        using var brush = new SolidBrush(UiTheme.Header);
        using var border = new Pen(UiTheme.Border);
        using var triangle = new SolidBrush(UiTheme.Muted);
        graphics.FillRectangle(brush, bounds);
        graphics.DrawLine(border, bounds.Right - 1, bounds.Top, bounds.Right - 1, bounds.Bottom);
        graphics.DrawLine(border, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);
        graphics.FillPolygon(triangle, [new Point(bounds.Right - 17, bounds.Bottom - 9), new Point(bounds.Right - 9, bounds.Bottom - 17), new Point(bounds.Right - 9, bounds.Bottom - 9)]);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _appearanceFont?.Dispose();
        base.Dispose(disposing);
    }
}
