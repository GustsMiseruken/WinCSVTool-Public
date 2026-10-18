using System.Runtime.InteropServices;

namespace WinCSVTool;

/// <summary>A native SQL editor with lightweight, debounced lexical coloring.</summary>
internal sealed class SqlEditor : RichTextBox
{
    internal enum TokenKind { Keyword, String, Comment, Number, Identifier, Function }
    internal readonly record struct Token(int Start, int Length, TokenKind Kind);
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "JOIN", "LEFT", "RIGHT", "INNER", "OUTER", "CROSS", "FULL", "ON", "AS", "AND", "OR", "NOT", "NULL",
        "IS", "IN", "EXISTS", "BETWEEN", "LIKE", "GLOB", "REGEXP", "ESCAPE", "DISTINCT", "ALL", "WITH", "RECURSIVE", "UNION", "INTERSECT",
        "EXCEPT", "ORDER", "BY", "ASC", "DESC", "NULLS", "FIRST", "LAST", "GROUP", "HAVING", "LIMIT", "OFFSET", "CASE", "WHEN", "THEN", "ELSE",
        "END", "CAST", "COLLATE", "OVER", "PARTITION", "ROWS", "RANGE", "UNBOUNDED", "PRECEDING", "FOLLOWING", "CURRENT", "ROW", "FILTER",
        "WINDOW", "VALUES", "TRUE", "FALSE", "INTEGER", "REAL", "TEXT", "BLOB", "NUMERIC", "NATURAL", "USING", "MATERIALIZED"
    };
    private readonly System.Windows.Forms.Timer _colorTimer = new() { Interval = 180 };
    private bool _coloring;
    private const int WmSetRedraw = 0x000B, EmGetScrollPos = 0x04DD, EmSetScrollPos = 0x04DE, EmGetOleInterface = 0x043C;

    public SqlEditor()
    {
        Dock = DockStyle.Fill;
        AcceptsTab = true;
        DetectUrls = false;
        WordWrap = false;
        ScrollBars = RichTextBoxScrollBars.Both;
        BorderStyle = BorderStyle.None;
        HideSelection = false;
        AutoWordSelection = false;
        MaxLength = int.MaxValue;
        _colorTimer.Tick += (_, _) => { _colorTimer.Stop(); HighlightNow(); };
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        if (_coloring) return;
        _colorTimer.Stop();
        _colorTimer.Start();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UiTheme.ApplyNativeTheme(this);
        _colorTimer.Start();
    }

    // RichEdit's built-in Ctrl+wheel zoom would bypass the application's preference.
    // The main window's message filter handles enabled document zoom consistently.
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if ((ModifierKeys & Keys.Control) == 0) base.OnMouseWheel(e);
    }

    internal void ApplyColors()
    {
        BackColor = UiTheme.Surface;
        ForeColor = UiTheme.Text;
        UiTheme.ApplyNativeTheme(this);
        HighlightNow();
    }

    internal void InsertIdentifier(string identifier)
    {
        if (ReadOnly || !Enabled) return;
        SelectedText = identifier;
        Focus();
    }

    internal void HighlightNow()
    {
        if (_coloring || IsDisposed || !IsHandleCreated) return;
        _colorTimer.Stop();
        var text = Text;
        var start = SelectionStart;
        var length = SelectionLength;
        var scroll = new NativePoint();
        _ = SendMessage(Handle, EmGetScrollPos, IntPtr.Zero, ref scroll);
        var document = GetTextDocument();
        // TOM suspends recording only for formatting and retains the existing edit history.
        // If the native document is unavailable, skip coloring rather than clearing Undo.
        if (document is null) return;
        var suspended = document.Undo(-9999995, out _) >= 0;
        if (!suspended) { Marshal.ReleaseComObject(document); return; }
        _coloring = true;
        _ = SendMessage(Handle, WmSetRedraw, IntPtr.Zero, IntPtr.Zero);
        try
        {
            SelectAll();
            SelectionColor = UiTheme.Text;
            foreach (var token in Tokenize(text))
            {
                Select(token.Start, token.Length);
                SelectionColor = TokenColor(token.Kind);
            }
            Select(start, length);
            if (length == 0) SelectionColor = UiTheme.Text;
        }
        finally
        {
            _ = document.Undo(-9999994, out _);
            Marshal.ReleaseComObject(document);
            _ = SendMessage(Handle, EmSetScrollPos, IntPtr.Zero, ref scroll);
            _ = SendMessage(Handle, WmSetRedraw, new IntPtr(1), IntPtr.Zero);
            _coloring = false;
            Invalidate();
        }
    }

    private static Color TokenColor(TokenKind kind) => (UiTheme.IsDark, kind) switch
    {
        (true, TokenKind.Keyword) => Color.FromArgb(86, 156, 214), (false, TokenKind.Keyword) => Color.FromArgb(33, 86, 183),
        (true, TokenKind.String) => Color.FromArgb(206, 145, 120), (false, TokenKind.String) => Color.FromArgb(163, 21, 21),
        (true, TokenKind.Comment) => Color.FromArgb(106, 153, 85), (false, TokenKind.Comment) => Color.FromArgb(58, 126, 64),
        (true, TokenKind.Number) => Color.FromArgb(181, 206, 168), (false, TokenKind.Number) => Color.FromArgb(9, 134, 88),
        (true, TokenKind.Identifier) => Color.FromArgb(156, 220, 254), (false, TokenKind.Identifier) => Color.FromArgb(21, 89, 117),
        (true, TokenKind.Function) => Color.FromArgb(220, 220, 170), (false, TokenKind.Function) => Color.FromArgb(121, 94, 38),
        _ => UiTheme.Text
    };

    /// <summary>One bounded linear scan; quoted strings/comments never become keywords.</summary>
    internal static IEnumerable<Token> Tokenize(string text)
    {
        // Very large scripts remain editable; their tail keeps the ordinary foreground color.
        var limit = Math.Min(text.Length, 250_000);
        for (var i = 0; i < limit;)
        {
            var start = i;
            var c = text[i];
            if (c == '-' && i + 1 < limit && text[i + 1] == '-')
            {
                i += 2;
                while (i < limit && text[i] is not ('\r' or '\n')) i++;
                yield return new(start, i - start, TokenKind.Comment);
            }
            else if (c == '/' && i + 1 < limit && text[i + 1] == '*')
            {
                i += 2;
                while (i < limit && !(text[i] == '*' && i + 1 < limit && text[i + 1] == '/')) i++;
                if (i < limit) i = Math.Min(limit, i + 2);
                yield return new(start, i - start, TokenKind.Comment);
            }
            else if (c is '\'' or '"' or '`' or '[')
            {
                var end = c == '[' ? ']' : c;
                i++;
                while (i < limit)
                {
                    if (text[i++] != end) continue;
                    if (c != '[' && i < limit && text[i] == end) { i++; continue; }
                    break;
                }
                yield return new(start, i - start, c == '\'' ? TokenKind.String : TokenKind.Identifier);
            }
            else if (char.IsDigit(c) || c == '.' && i + 1 < limit && char.IsDigit(text[i + 1]))
            {
                i++;
                while (i < limit && (char.IsDigit(text[i]) || text[i] == '.')) i++;
                if (i < limit && text[i] is 'e' or 'E')
                {
                    var exponent = i++;
                    if (i < limit && text[i] is '+' or '-') i++;
                    var digits = i;
                    while (i < limit && char.IsDigit(text[i])) i++;
                    if (digits == i) i = exponent;
                }
                yield return new(start, i - start, TokenKind.Number);
            }
            else if (char.IsLetter(c) || c == '_')
            {
                i++;
                while (i < limit && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '$')) i++;
                var word = text[start..i];
                if (Keywords.Contains(word)) yield return new(start, i - start, TokenKind.Keyword);
                else
                {
                    var next = i;
                    while (next < limit && char.IsWhiteSpace(text[next])) next++;
                    if (next < limit && text[next] == '(') yield return new(start, i - start, TokenKind.Function);
                }
            }
            else i++;
        }
    }

    private ITextDocument? GetTextDocument()
    {
        IntPtr ole = IntPtr.Zero;
        _ = SendMessage(Handle, EmGetOleInterface, IntPtr.Zero, ref ole);
        if (ole == IntPtr.Zero) return null;
        try { return Marshal.GetObjectForIUnknown(ole) as ITextDocument; }
        catch (InvalidCastException) { return null; }
        finally { Marshal.Release(ole); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _colorTimer.Dispose();
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X; internal int Y; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, ref NativePoint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, ref IntPtr lParam);

    // The standard RichEdit Text Object Model prefix through Undo. All TOM longs are 32-bit.
    [ComImport, Guid("8CC497C0-A1DF-11CE-8098-00AA0047BE5D"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface ITextDocument
    {
        [PreserveSig] int GetName(out IntPtr value);
        [PreserveSig] int GetSelection(out IntPtr value);
        [PreserveSig] int GetStoryCount(out int value);
        [PreserveSig] int GetStoryRanges(out IntPtr value);
        [PreserveSig] int GetSaved(out int value);
        [PreserveSig] int SetSaved(int value);
        [PreserveSig] int GetDefaultTabStop(out float value);
        [PreserveSig] int SetDefaultTabStop(float value);
        [PreserveSig] int New();
        [PreserveSig] int Open(IntPtr value, int flags, int codePage);
        [PreserveSig] int Save(IntPtr value, int flags, int codePage);
        [PreserveSig] int Freeze(out int count);
        [PreserveSig] int Unfreeze(out int count);
        [PreserveSig] int BeginEditCollection();
        [PreserveSig] int EndEditCollection();
        [PreserveSig] int Undo(int count, out int applied);
    }
}

/// <summary>Quiet dotted underlines mark the schema identifiers that can be inserted.</summary>
internal sealed class SqlSchemaTree : TreeView
{
    public SqlSchemaTree()
    {
        Dock = DockStyle.Fill;
        BorderStyle = BorderStyle.None;
        HideSelection = false;
        ShowNodeToolTips = true;
        DrawMode = TreeViewDrawMode.OwnerDrawText;
        FullRowSelect = false;
        Indent = 18;
        ItemHeight = 28;
        DrawNode += DrawIdentifier;
    }

    private void DrawIdentifier(object? sender, DrawTreeNodeEventArgs e)
    {
        if (e.Node is not { } node) return;
        var selected = (e.State & TreeNodeStates.Selected) != 0;
        var background = selected ? UiTheme.Selection : BackColor;
        using var brush = new SolidBrush(background);
        e.Graphics.FillRectangle(brush, e.Bounds);
        var color = node.Tag is string ? UiTheme.Text : UiTheme.Muted;
        TextRenderer.DrawText(e.Graphics, node.Text, node.NodeFont ?? Font, e.Bounds, color,
            TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        if (node.Tag is string && e.Bounds.Width > 0)
        {
            using var pen = new Pen(UiTheme.Muted) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
            e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 3, e.Bounds.Right, e.Bounds.Bottom - 3);
        }
        if ((e.State & TreeNodeStates.Focused) != 0 && Focused) ControlPaint.DrawFocusRectangle(e.Graphics, e.Bounds, UiTheme.Text, background);
    }
}
