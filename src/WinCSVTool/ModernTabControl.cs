using System.Drawing.Drawing2D;

namespace WinCSVTool;

/// <summary>Keeps native tab navigation, with flat tabs and a per-file close target.</summary>
internal sealed class ModernTabControl : TabControl
{
    private int _hoverIndex = -1;
    private int _hoverClose = -1;
    private TabPage? _pressedClose;
    private bool _sheetStyle;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool SheetStyle { get => _sheetStyle; set { _sheetStyle = value; Alignment = value ? TabAlignment.Bottom : TabAlignment.Top; UpdateMetrics(); } }
    internal event Action<TabPage>? CloseRequested;

    internal ModernTabControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        DrawMode = TabDrawMode.OwnerDrawFixed;
        SizeMode = TabSizeMode.Fixed;
        Multiline = false;
        HotTrack = true;
        ShowToolTips = true;
        Font = new Font("Yu Gothic UI", 9.5f);
        BackColor = UiTheme.Bg;
        ForeColor = UiTheme.Text;
        UpdateMetrics();
    }

    private int Px(int value) => (int)Math.Round(value * DeviceDpi / 96f);

    private void UpdateMetrics()
    {
        ItemSize = new Size(Px(_sheetStyle ? 148 : 190), Px(_sheetStyle ? 31 : 37));
        Padding = new Point(Px(14), Px(7));
        Invalidate();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        UpdateMetrics();
    }

    protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(UiTheme.Bg);

    protected override void OnPaint(PaintEventArgs e)
    {
        using var surface = new SolidBrush(UiTheme.Surface);
        using var border = new Pen(UiTheme.Border);
        var pageArea = DisplayRectangle;
        e.Graphics.FillRectangle(surface, pageArea);
        var edge = _sheetStyle ? pageArea.Bottom : pageArea.Top - 1;
        e.Graphics.DrawLine(border, 0, edge, Width, edge);
        for (var i = 0; i < TabCount; i++) DrawTab(e.Graphics, i);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index >= 0 && e.Index < TabCount) DrawTab(e.Graphics, e.Index);
    }

    private void DrawTab(Graphics graphics, int index)
    {
        var rect = GetTabRect(index);
        if (!rect.IntersectsWith(ClientRectangle)) return;
        var selected = index == SelectedIndex;
        var page = TabPages[index];
        using var back = new SolidBrush(selected ? UiTheme.Surface : index == _hoverIndex ? UiTheme.Hover : UiTheme.Bg);
        graphics.FillRectangle(back, rect);
        using var border = new Pen(UiTheme.Border);
        graphics.DrawLine(border, rect.Right - 1, rect.Top + Px(9), rect.Right - 1, rect.Bottom - Px(9));
        if (selected)
        {
            using var accent = new SolidBrush(UiTheme.Accent);
            graphics.FillRectangle(accent, rect.Left, rect.Top, rect.Width, Px(3));
        }
        using var icon = UiTheme.CreateIcon("table", selected ? UiTheme.Accent : UiTheme.Muted, Px(16));
        graphics.DrawImageUnscaled(icon, rect.Left + Px(13), rect.Top + (rect.Height - icon.Height) / 2);
        var isDirty = page.Text.EndsWith('*');
        var text = isDirty ? page.Text.TrimEnd('*').TrimEnd() : page.Text;
        var close = CloseBounds(index);
        var textBounds = new Rectangle(rect.Left + Px(37), rect.Top + Px(2), Math.Max(1, close.Left - rect.Left - Px(43)), rect.Height - Px(4));
        TextRenderer.DrawText(graphics, text, Font, textBounds, selected ? UiTheme.Text : UiTheme.Muted,
            TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        if (isDirty)
        {
            using var dirty = new SolidBrush(UiTheme.Accent);
            graphics.FillEllipse(dirty, close.Left - Px(8), close.Top + Px(7), Px(5), Px(5));
        }
        if (page.Tag is false) return;
        if (index == _hoverClose)
        {
            using var hover = new SolidBrush(UiTheme.Hover);
            graphics.FillRectangle(hover, close);
        }
        using var cross = new Pen(index == _hoverClose ? UiTheme.Text : UiTheme.Muted, Math.Max(1, DeviceDpi / 96f * 1.3f))
        { StartCap = LineCap.Round, EndCap = LineCap.Round };
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.DrawLine(cross, close.Left + Px(6), close.Top + Px(6), close.Right - Px(6), close.Bottom - Px(6));
        graphics.DrawLine(cross, close.Right - Px(6), close.Top + Px(6), close.Left + Px(6), close.Bottom - Px(6));
        graphics.SmoothingMode = SmoothingMode.Default;
        if (selected && Focused && ShowFocusCues)
        {
            var focus = textBounds;
            focus.Inflate(-Px(1), -Px(7));
            ControlPaint.DrawFocusRectangle(graphics, focus, UiTheme.Muted, UiTheme.Surface);
        }
    }

    internal Rectangle CloseBounds(int index)
    {
        var rect = GetTabRect(index);
        var edge = Px(22);
        return new Rectangle(rect.Right - edge - Px(8), rect.Top + (rect.Height - edge) / 2, edge, edge);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var hover = -1;
        var close = -1;
        for (var i = 0; i < TabCount; i++)
                if (GetTabRect(i).Contains(e.Location)) { hover = i; if (TabPages[i].Tag is not false && CloseBounds(i).Contains(e.Location)) close = i; break; }
        if (hover != _hoverIndex || close != _hoverClose)
        {
            _hoverIndex = hover; _hoverClose = close;
            Cursor = close >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hoverIndex = _hoverClose = -1;
        Cursor = Cursors.Default;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
            for (var i = 0; i < TabCount; i++)
                if (TabPages[i].Tag is not false && CloseBounds(i).Contains(e.Location)) { _pressedClose = TabPages[i]; break; }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        var pressed = _pressedClose;
        _pressedClose = null;
        base.OnMouseUp(e);
        if (pressed is null || e.Button != MouseButtons.Left) return;
        var index = TabPages.IndexOf(pressed);
        if (index >= 0 && CloseBounds(index).Contains(e.Location)) CloseRequested?.Invoke(pressed);
    }

    protected override void OnSelectedIndexChanged(EventArgs e)
    {
        base.OnSelectedIndexChanged(e);
        Invalidate();
    }

    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        if (e.Control is TabPage page)
        {
            page.TextChanged += PageTextChanged;
            page.ToolTipText = page.Text;
        }
        Invalidate();
    }

    protected override void OnControlRemoved(ControlEventArgs e)
    {
        if (e.Control is TabPage page) page.TextChanged -= PageTextChanged;
        _hoverIndex = _hoverClose = -1;
        base.OnControlRemoved(e);
        Invalidate();
    }

    private void PageTextChanged(object? sender, EventArgs e)
    {
        if (sender is TabPage page) page.ToolTipText = page.Text;
        Invalidate();
    }
}
