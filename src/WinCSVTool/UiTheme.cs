using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinCSVTool;

internal interface IThemeOwnedControl
{
    void ApplyTheme();
}

/// <summary>Small native drawing helpers shared by the shell and dialogs.</summary>
internal static class UiTheme
{
    private sealed record IconPalette(bool Dark);
    private static readonly ConditionalWeakTable<Image, IconPalette> IconPalettes = new();
    private sealed class NativeThemeState { internal IntPtr Handle; internal bool Dark; }
    private static readonly ConditionalWeakTable<Control, NativeThemeState> NativeThemes = new();
    internal static bool IsDark { get; private set; }
    internal static Color Bg => IsDark ? Color.FromArgb(25, 28, 35) : Color.FromArgb(244, 246, 250);
    internal static Color Background => Bg;
    internal static Color Surface => IsDark ? Color.FromArgb(31, 35, 43) : Color.White;
    internal static Color Header => IsDark ? Color.FromArgb(37, 42, 52) : Color.FromArgb(245, 247, 251);
    internal static Color Border => IsDark ? Color.FromArgb(66, 74, 89) : Color.FromArgb(220, 226, 235);
    internal static Color Text => IsDark ? Color.FromArgb(229, 234, 244) : Color.FromArgb(32, 41, 57);
    internal static Color Muted => IsDark ? Color.FromArgb(163, 175, 194) : Color.FromArgb(105, 117, 134);
    internal static Color Accent => IsDark ? Color.FromArgb(155, 171, 255) : Color.FromArgb(66, 85, 212);
    internal static Color AccentSoft => IsDark ? Color.FromArgb(49, 58, 88) : Color.FromArgb(238, 240, 255);
    internal static Color Selection => IsDark ? Color.FromArgb(54, 67, 99) : Color.FromArgb(234, 240, 255);
    internal static Color Hover => IsDark ? Color.FromArgb(47, 54, 67) : Color.FromArgb(233, 237, 245);
    internal static Color GridLine => IsDark ? Color.FromArgb(51, 58, 70) : Color.FromArgb(229, 233, 240);
    internal static Color Success => IsDark ? Color.FromArgb(105, 211, 175) : Color.FromArgb(30, 133, 105);

    internal static void SetTheme(AppTheme theme)
    {
        IsDark = theme == AppTheme.Dark || theme == AppTheme.System && SystemUsesDarkTheme();
    }

    private static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return false; }
    }

    internal static Font CreateGridFont(AppSettings settings) => CreateFont(settings.GridFontFamily,
        settings.GridFontSize * Math.Clamp(settings.ZoomPercent, 50, 200) / 100f, "Yu Gothic UI");

    internal static Font CreateEditorFont(AppSettings settings) => CreateFont(settings.EditorFontFamily,
        settings.EditorFontSize * Math.Clamp(settings.ZoomPercent, 50, 200) / 100f, "Consolas");

    private static Font CreateFont(string family, float size, string fallback)
    {
        try { return new Font(family, size, FontStyle.Regular, GraphicsUnit.Point); }
        catch (ArgumentException) { return new Font(fallback, size, FontStyle.Regular, GraphicsUnit.Point); }
    }

    internal static void ThemeForm(Form form, bool recursive = true)
    {
        form.BackColor = Surface;
        form.ForeColor = Text;
        form.Font = new Font("Yu Gothic UI", 9.5f);
        ApplyTitleBar(form);
        if (recursive) foreach (Control child in form.Controls) Apply(child);
    }

    private static void ApplyTitleBar(Form form)
    {
        form.HandleCreated -= FormHandleCreated;
        form.HandleCreated += FormHandleCreated;
        if (!form.IsHandleCreated) return;
        var dark = IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(form.Handle, 20, ref dark, sizeof(int));
    }

    private static void FormHandleCreated(object? sender, EventArgs e)
    {
        if (sender is Form form) ApplyTitleBar(form);
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    internal static void Apply(Control control, bool recursive = true)
    {
        if (control is IThemeOwnedControl custom)
        {
            custom.ApplyTheme();
            if (recursive) foreach (Control child in control.Controls) Apply(child);
            control.Invalidate();
            return;
        }
        control.ForeColor = Text;
        switch (control)
        {
            case Form form:
                form.BackColor = Surface;
                ApplyTitleBar(form);
                break;
            case ToolStrip strip: ApplyToolStrip(strip); break;
            case Button button: StyleButton(button, button.DialogResult == DialogResult.OK); break;
            case TextBoxBase text:
                text.BackColor = Surface;
                text.BorderStyle = BorderStyle.FixedSingle;
                break;
            case ComboBox combo:
                StyleCombo(combo);
                break;
            case ListBox list:
                list.BackColor = Surface;
                list.BorderStyle = BorderStyle.FixedSingle;
                break;
            case CheckBox check:
                check.FlatStyle = FlatStyle.Flat;
                check.BackColor = Color.Transparent;
                break;
            case RadioButton radio:
                radio.FlatStyle = FlatStyle.Flat;
                radio.BackColor = Color.Transparent;
                break;
            case DataGridView grid:
                StyleGrid(grid);
                break;
            case NumericUpDown numeric:
                numeric.BackColor = Surface;
                numeric.BorderStyle = BorderStyle.FixedSingle;
                break;
            case TreeView tree:
                tree.BackColor = Surface;
                tree.BorderStyle = BorderStyle.FixedSingle;
                tree.LineColor = Border;
                break;
            case ModernTabControl tabs:
                tabs.BackColor = Bg;
                tabs.Invalidate();
                break;
            case SplitContainer split:
                split.BackColor = Border;
                split.Panel1.BackColor = Surface;
                split.Panel2.BackColor = Surface;
                break;
            case Label:
                control.BackColor = Color.Transparent;
                break;
            case Panel or TabPage or GroupBox:
                control.BackColor = Surface;
                break;
        }
        ApplyNativeTheme(control);
        if (recursive) foreach (Control child in control.Controls) Apply(child);
        control.Invalidate();
    }

    /// <summary>Restyles a virtual grid without changing its rows, fonts, or selection.</summary>
    internal static void StyleGrid(DataGridView grid)
    {
        grid.EnableHeadersVisualStyles = false;
        grid.BackgroundColor = Surface;
        grid.GridColor = GridLine;
        // WinForms can return a merged copy when alignment/wrap inherit defaults.
        // Assign the complete style back so colors persist for every grid shape.
        var cells = grid.DefaultCellStyle;
        cells.BackColor = Surface; cells.ForeColor = Text;
        cells.SelectionBackColor = Selection; cells.SelectionForeColor = Text;
        grid.DefaultCellStyle = cells;
        var alternating = grid.AlternatingRowsDefaultCellStyle;
        alternating.BackColor = Surface; alternating.ForeColor = Text;
        grid.AlternatingRowsDefaultCellStyle = alternating;
        var columns = grid.ColumnHeadersDefaultCellStyle;
        columns.BackColor = Header; columns.ForeColor = Text;
        columns.SelectionBackColor = Header; columns.SelectionForeColor = Text;
        grid.ColumnHeadersDefaultCellStyle = columns;
        var rows = grid.RowHeadersDefaultCellStyle;
        rows.BackColor = Header; rows.ForeColor = Muted;
        rows.SelectionBackColor = AccentSoft; rows.SelectionForeColor = Text;
        grid.RowHeadersDefaultCellStyle = rows;
        grid.Invalidate();
    }

    private static void StyleCombo(ComboBox combo)
    {
        combo.BackColor = Surface; combo.ForeColor = Text; combo.FlatStyle = FlatStyle.Flat;
        if (combo.DropDownStyle != ComboBoxStyle.DropDownList) return;
        // Native read-only combo selection fields may keep a white background
        // even after a dark theme switch. Draw both selection and popup items.
        combo.DrawMode = DrawMode.OwnerDrawFixed;
        combo.DrawItem -= DrawComboItem; combo.DrawItem += DrawComboItem;
    }

    private static void DrawComboItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ComboBox combo) return;
        var selected = (e.State & DrawItemState.Selected) != 0;
        using var background = new SolidBrush(selected ? Selection : Surface);
        e.Graphics.FillRectangle(background, e.Bounds);
        var text = e.Index >= 0 && e.Index < combo.Items.Count ? combo.GetItemText(combo.Items[e.Index]) : combo.Text;
        var bounds = e.Bounds; bounds.Inflate(-3, 0);
        TextRenderer.DrawText(e.Graphics, text, e.Font ?? combo.Font, bounds, combo.Enabled ? Text : Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
    }

    internal static void StyleButton(Button button, bool primary = false)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.MouseOverBackColor = primary ? Selection : Hover;
        button.FlatAppearance.MouseDownBackColor = Selection;
        button.BackColor = primary ? AccentSoft : Surface;
        button.ForeColor = Text;
        button.Padding = new Padding(8, 2, 8, 2);
        button.Cursor = Cursors.Hand;
    }

    /// <summary>Theme both native edit/tree scrollbars and the grid's scrollbar child windows.</summary>
    internal static void ApplyNativeTheme(Control control)
    {
        if (!NativeThemes.TryGetValue(control, out var state))
        {
            NativeThemes.Add(control, state = new NativeThemeState());
            control.HandleCreated += NativeHandleCreated;
            control.HandleDestroyed += NativeHandleDestroyed;
            control.ControlAdded += NativeChildAdded;
        }
        if (!control.IsHandleCreated || state.Handle == control.Handle && state.Dark == IsDark) return;
        state.Handle = control.Handle;
        state.Dark = IsDark;
        _ = SetWindowTheme(control.Handle, IsDark ? control is ComboBox ? "DarkMode_CFD" : "DarkMode_Explorer" : "Explorer", control is ScrollBar ? "ScrollBar" : null);
        control.Invalidate();
    }

    private static void NativeHandleCreated(object? sender, EventArgs e)
    {
        if (sender is Control control) ApplyNativeTheme(control);
    }

    private static void NativeHandleDestroyed(object? sender, EventArgs e)
    {
        if (sender is Control control && NativeThemes.TryGetValue(control, out var state)) state.Handle = IntPtr.Zero;
    }

    private static void NativeChildAdded(object? sender, ControlEventArgs e)
    {
        ApplyNativeTheme(e.Control!);
        foreach (Control child in e.Control!.Controls) ApplyNativeTheme(child);
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetWindowTheme(IntPtr window, string? subAppName, string? subIdList);

    internal static void ApplyToolStrip(ToolStrip strip)
    {
        strip.Renderer = new FlatStripRenderer();
        strip.BackColor = strip is ToolStripDropDown ? Surface : Bg;
        strip.ForeColor = Text;
        strip.GripStyle = ToolStripGripStyle.Hidden;
        foreach (ToolStripItem item in strip.Items) StyleItem(item);
        strip.ItemAdded -= StripItemAdded;
        strip.ItemAdded += StripItemAdded;
    }

    private static void StripItemAdded(object? sender, ToolStripItemEventArgs e)
    {
        if (e.Item is { } item) StyleItem(item);
    }

    private static void StyleItem(ToolStripItem item)
    {
        item.ForeColor = Text;
        if (item is ToolStripTextBox text)
        {
            text.BackColor = Surface;
            text.ForeColor = Text;
            text.BorderStyle = BorderStyle.FixedSingle;
        }
        if (item is ToolStripComboBox combo)
        {
            StyleCombo(combo.ComboBox);
            ApplyNativeTheme(combo.ComboBox);
        }
        if (item is ToolStripDropDownItem drop)
        {
            drop.DropDown.Renderer = new FlatStripRenderer();
            drop.DropDown.BackColor = Surface;
            drop.DropDown.ForeColor = Text;
            foreach (ToolStripItem child in drop.DropDownItems) StyleItem(child);
        }
    }

    /// <summary>Original line icons, drawn at the requested DPI with no asset or font dependency.</summary>
    internal static Bitmap CreateIcon(string key, Color? color = null, int size = 18)
    {
        size = Math.Max(12, size);
        var bitmap = new Bitmap(size, size);
        IconPalettes.Add(bitmap, new IconPalette(IsDark));
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.ScaleTransform(size / 20f, size / 20f);
        using var pen = new Pen(color ?? Text, 1.45f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        void Line(float x1, float y1, float x2, float y2) => g.DrawLine(pen, x1, y1, x2, y2);
        void Poly(params PointF[] points) => g.DrawLines(pen, points);
        void File()
        {
            Poly(new(5, 2.5f), new(12, 2.5f), new(16, 6.5f), new(16, 17.5f), new(5, 17.5f), new(5, 2.5f));
            Poly(new(12, 2.5f), new(12, 6.5f), new(16, 6.5f));
        }
        switch (key.ToLowerInvariant())
        {
            case "open":
                Poly(new(2.5f, 15.5f), new(2.5f, 5), new(7.5f, 5), new(9.5f, 7), new(16, 7), new(16, 9));
                Poly(new(2.5f, 16), new(5.5f, 9), new(18, 9), new(15, 16), new(2.5f, 16));
                break;
            case "new": File(); Line(8, 11.5f, 13, 11.5f); Line(10.5f, 9, 10.5f, 14); break;
            case "save":
                Poly(new(3.5f, 3), new(14, 3), new(17, 6), new(17, 17), new(3.5f, 17), new(3.5f, 3));
                g.DrawRectangle(pen, 7, 3, 6, 5); g.DrawRectangle(pen, 7, 12, 7, 5); break;
            case "search": g.DrawEllipse(pen, 3, 2.5f, 10, 10); Line(11.5f, 11, 17, 16.5f); break;
            case "decode":
                Poly(new(6, 5), new(2, 10), new(6, 15)); Poly(new(14, 5), new(18, 10), new(14, 15));
                Line(11.5f, 4, 8.5f, 16); break;
            case "filter":
                Poly(new(2.5f, 4), new(17.5f, 4), new(11.5f, 10.5f), new(11.5f, 16), new(8.5f, 14.5f), new(8.5f, 10.5f), new(2.5f, 4)); break;
            case "type": Line(3, 5, 13, 5); Line(8, 5, 8, 16); Line(5, 16, 11, 16); Line(15, 10, 18, 10); Line(16.5f, 10, 16.5f, 16); break;
            case "stats": Line(3, 17, 18, 17); g.DrawRectangle(pen, 5, 10, 2.5f, 7); g.DrawRectangle(pen, 10, 4, 2.5f, 13); g.DrawRectangle(pen, 15, 7, 2.5f, 10); break;
            case "sql":
                g.DrawEllipse(pen, 3.5f, 3, 13, 4.5f); Line(3.5f, 5.2f, 3.5f, 14.8f); Line(16.5f, 5.2f, 16.5f, 14.8f);
                g.DrawArc(pen, 3.5f, 8, 13, 4.5f, 0, 180); g.DrawArc(pen, 3.5f, 12.5f, 13, 4.5f, 0, 180); break;
            case "close": case "cancel": Line(5, 5, 15, 15); Line(15, 5, 5, 15); break;
            case "left": case "previous": case "chevron-left": case "first":
                Poly(new(12.5f, 4.5f), new(7, 10), new(12.5f, 15.5f)); if (key.Equals("first", StringComparison.OrdinalIgnoreCase)) Line(4, 4.5f, 4, 15.5f); break;
            case "right": case "next": case "chevron-right": case "last":
                Poly(new(7.5f, 4.5f), new(13, 10), new(7.5f, 15.5f)); if (key.Equals("last", StringComparison.OrdinalIgnoreCase)) Line(16, 4.5f, 16, 15.5f); break;
            case "wrap":
                Line(3, 5, 17, 5); Poly(new(3, 10), new(14, 10), new(16, 11.5f), new(16, 14), new(10, 14));
                Poly(new(12.5f, 11.5f), new(10, 14), new(12.5f, 16.5f)); Line(3, 15, 6.5f, 15); break;
            case "run": g.DrawPolygon(pen, [new PointF(6, 3), new PointF(16, 10), new PointF(6, 17)]); break;
            case "table":
                g.DrawRectangle(pen, 3, 3, 14, 14); Line(3, 8, 17, 8); Line(3, 12.5f, 17, 12.5f); Line(8, 3, 8, 17); Line(12.5f, 3, 12.5f, 17); break;
            case "settings":
                Line(3, 5, 17, 5); Line(3, 10, 17, 10); Line(3, 15, 17, 15);
                using (var fill = new SolidBrush(Surface))
                    foreach (var point in new[] { new Point(7, 5), new Point(13, 10), new Point(8, 15) })
                    { g.FillEllipse(fill, point.X - 2, point.Y - 2, 4, 4); g.DrawEllipse(pen, point.X - 2, point.Y - 2, 4, 4); }
                break;
            case "properties":
                g.DrawEllipse(pen, 3, 3, 14, 14); Line(10, 9, 10, 14); g.DrawEllipse(pen, 9.5f, 6, 1, 1); break;
            case "zoom-in": case "zoom-out":
                g.DrawEllipse(pen, 2.5f, 2.5f, 11, 11); Line(12, 12, 17, 17); Line(5, 8, 11, 8);
                if (key.Equals("zoom-in", StringComparison.OrdinalIgnoreCase)) Line(8, 5, 8, 11);
                break;
            case "arrow-up": Poly(new(5, 9), new(10, 4), new(15, 9)); Line(10, 4, 10, 17); break;
            case "arrow-down": Poly(new(5, 11), new(10, 16), new(15, 11)); Line(10, 3, 10, 16); break;
            default: File(); break;
        }
        return bitmap;
    }

    private sealed class FlatStripRenderer : ToolStripProfessionalRenderer
    {
        internal FlatStripRenderer() : base(new FlatColors()) { RoundedEdges = false; }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(e.ToolStrip.BackColor);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(Border);
            if (e.ToolStrip is ToolStripDropDown)
                e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            else if (e.ToolStrip is StatusStrip)
                e.Graphics.DrawLine(pen, 0, 0, e.ToolStrip.Width, 0);
            else if (e.ToolStrip is not MenuStrip)
                e.Graphics.DrawLine(pen, 0, e.ToolStrip.Height - 1, e.ToolStrip.Width, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(Border);
            if (e.Vertical) e.Graphics.DrawLine(pen, e.Item.Width / 2, 7, e.Item.Width / 2, e.Item.Height - 7);
            else e.Graphics.DrawLine(pen, 12, e.Item.Height / 2, e.Item.Width - 8, e.Item.Height / 2);
        }

        protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
        {
            var selected = e.Item.Selected || e.Item.Pressed;
            var check = e.Item is ToolStripButton { Checked: true };
            if (!selected && !check) return;
            var bounds = new Rectangle(1, 2, e.Item.Width - 2, e.Item.Height - 4);
            using var brush = new SolidBrush(check || e.Item.Pressed ? Selection : Hover);
            e.Graphics.FillRectangle(brush, bounds);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? e.Item.ForeColor : Muted;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
        {
            // Line icons created before a theme switch remain legible. This is
            // a rendering transform only, so repeated switching never alters assets.
            if (e.Image is null || !IconPalettes.TryGetValue(e.Image, out var palette) || palette.Dark == IsDark)
            { base.OnRenderItemImage(e); return; }
            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(new ColorMatrix([
                [-1, 0, 0, 0, 0], [0, -1, 0, 0, 0], [0, 0, -1, 0, 0],
                [0, 0, 0, 1, 0], [1, 1, 1, 0, 1]
            ]));
            e.Graphics.DrawImage(e.Image, e.ImageRectangle, 0, 0, e.Image.Width, e.Image.Height, GraphicsUnit.Pixel, attributes);
        }
    }

    private sealed class FlatColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemBorder => Hover;
        public override Color MenuBorder => Border;
        public override Color ToolStripDropDownBackground => Surface;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Selection;
        public override Color MenuItemPressedGradientMiddle => Selection;
        public override Color MenuItemPressedGradientEnd => Selection;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color CheckBackground => Selection;
        public override Color CheckSelectedBackground => Selection;
        public override Color CheckPressedBackground => Selection;
    }
}
