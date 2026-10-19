using WinCSVTool.Core;

namespace WinCSVTool;

internal sealed partial class MainForm
{
    internal async Task RefreshLogColumnsAsync(CsvDocument document)
    {
        foreach (var page in DocumentPages.Where(page => page.Document == document))
        {
            page.InvalidateDisplayedRows(); page.BuildColumns(); page.ApplyAppearance(Settings);
            await page.ReloadAsync(canCancel: false); page.UpdateTitle();
        }
        _sqlForm?.RefreshSchema(); _logAnalysis?.RefreshDocuments(document); UpdateInspector();
    }
}

/// <summary>Navigation only: constructing or selecting a feature never starts analysis.</summary>
internal sealed class LogAnalysisPage : UserControl
{
    private readonly TreeView _navigation = new() { Dock = DockStyle.Fill, HideSelection = false, FullRowSelect = true, BorderStyle = BorderStyle.None, ShowLines = false };
    private readonly Panel _host = new FeaturePanel { Dock = DockStyle.Fill };
    private readonly SplitContainer _split = new() { Dock = DockStyle.Fill, Size = new Size(1100, 700), SplitterDistance = 205, Panel1MinSize = 135, Panel2MinSize = 240, SplitterWidth = 5 };
    private readonly IpLogAnalysisPage _ip;
    private readonly Dictionary<string, Control> _features = [];

    internal LogAnalysisPage(MainForm owner)
    {
        Dock = DockStyle.Fill; DoubleBuffered = true;
        _ip = new IpLogAnalysisPage(owner);
        _features.Add("import", new LogImportLandingPage(owner));
        _features.Add("ip", _ip);
        _features.Add("time", new LogTimePage(owner));
        _features.Add("stats", new LogStatisticsPage(owner));
        _features.Add("series", new LogTimeSeriesPage(owner));
        _features.Add("attack", new LogAttackPage(owner));
        var prepare = _navigation.Nodes.Add("データの準備");
        prepare.Nodes.Add(new TreeNode("アクセスログを開く") { Tag = "import" });
        prepare.Nodes.Add(new TreeNode("IP・ASN") { Tag = "ip" });
        prepare.Nodes.Add(new TreeNode("時刻の正規化・変換") { Tag = "time" });
        var analysis = _navigation.Nodes.Add("統計・検出");
        analysis.Nodes.Add(new TreeNode("基本統計") { Tag = "stats" });
        analysis.Nodes.Add(new TreeNode("グラフで調査") { Tag = "series" });
        analysis.Nodes.Add(new TreeNode("攻撃パターン分類") { Tag = "attack" });
        _navigation.ExpandAll(); _navigation.AfterSelect += (_, e) => { if (e.Node?.Tag is string key) ShowFeature(key); };
        _split.Panel1.Controls.Add(_navigation); _split.Panel2.Controls.Add(_host); Controls.Add(_split);
        SizeChanged += (_, _) => { if (Width > 420) _split.SplitterDistance = Math.Clamp((int)Math.Round(175 * DeviceDpi / 96f), 135, Math.Max(135, Width - 245)); };
        SelectFeature("ip");
    }

    internal void SelectFeature(string key)
    {
        var node = _navigation.Nodes.Cast<TreeNode>().SelectMany(group => group.Nodes.Cast<TreeNode>()).FirstOrDefault(node => Equals(node.Tag, key));
        if (node is not null) _navigation.SelectedNode = node;
        ShowFeature(key);
    }

    private void ShowFeature(string key)
    {
        if (!_features.TryGetValue(key, out var control)) return;
        if (_host.Controls.Count == 1 && _host.Controls[0] == control) return;
        _host.SuspendLayout();
        try { _host.Controls.Clear(); control.Dock = DockStyle.Fill; _host.Controls.Add(control); }
        finally { _host.ResumeLayout(true); }
    }

    internal void RefreshDocuments(CsvDocument? selected = null)
    {
        _ip.RefreshDocuments(selected);
        foreach (var feature in _features.Values.OfType<LogFeaturePage>()) feature.RefreshDocuments(selected);
    }

    internal void ApplyAppearance()
    {
        UiTheme.Apply(this); _navigation.BackColor = UiTheme.Header; _navigation.ForeColor = UiTheme.Text;
        _navigation.ItemHeight = Math.Max(30, Font.Height + 12); _ip.ApplyAppearance();
        foreach (var feature in _features.Values.OfType<LogFeaturePage>()) feature.ApplyAppearance();
    }

    internal void RefreshAnalysisValidity()
    {
        foreach (var feature in _features.Values.OfType<LogTimeSeriesPage>()) feature.RefreshValidity();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) foreach (var feature in _features.Values) if (feature.Parent is null) feature.Dispose();
        base.Dispose(disposing);
    }
}

internal abstract class LogFeaturePage : UserControl
{
    protected readonly MainForm Owner;
    protected readonly ComboBox Documents = Choice();
    protected readonly TableLayoutPanel Inputs = new() { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 6, 0, 6) };
    protected readonly Label Status = new() { AutoSize = true, MaximumSize = new Size(1100, 0), Text = "設定を選び、［実行］を押してください。", Margin = new Padding(0, 8, 0, 8) };
    protected readonly FlowLayoutPanel Actions = new() { AutoSize = true, Dock = DockStyle.Top, WrapContents = true, Margin = Padding.Empty };
    protected readonly TableLayoutPanel Content = new FeatureTable { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(16), Margin = Padding.Empty };
    private sealed record DocumentChoice(CsvDocument Document) { public override string ToString() => Document.DisplayName + "  (" + Document.TableName + ")"; }
    private sealed class ColumnBinding(ComboBox choice, bool optional)
    {
        internal ComboBox Choice = choice;
        internal bool Optional = optional;
        internal CsvDocument? Document;
        internal string[] Names = [];
    }
    private readonly List<ColumnBinding> _columns = [];
    private bool _actionRunning;
    protected bool Refreshing { get; private set; }
    protected CsvDocument? Document => (Documents.SelectedItem as DocumentChoice)?.Document;

    protected LogFeaturePage(MainForm owner, string title, string description)
    {
        Owner = owner; Dock = DockStyle.Fill; DoubleBuffered = true;
        var scroll = new FeaturePanel { Dock = DockStyle.Fill, AutoScroll = true }; scroll.Controls.Add(Content); Controls.Add(scroll);
        Content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddContent(new Label { Text = title, AutoSize = true, Font = new Font(owner.Font.FontFamily, 13, FontStyle.Bold), Margin = new Padding(0, 0, 0, 8) });
        AddContent(new Label { Text = description, AutoSize = true, MaximumSize = new Size(1000, 0), Margin = new Padding(0, 0, 0, 8) });
        Inputs.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); Inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddContent(Inputs); AddContent(Actions); AddContent(Status);
        AddField("解析対象の表", Documents);
        Documents.SelectedIndexChanged += (_, _) => { if (!Refreshing) { RefreshColumns(); DocumentChanged(); } };
    }

    internal static ComboBox Choice() => new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, MinimumSize = new Size(180, 0) };
    protected void AddContent(Control control) { control.Dock = DockStyle.Top; Content.RowStyles.Add(new RowStyle(SizeType.AutoSize)); Content.Controls.Add(control, 0, Content.RowCount++); }
    protected void AddField(string title, Control control)
    {
        control.AccessibleName = title;
        var row = Inputs.RowCount++; Inputs.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Inputs.Controls.Add(new Label { Text = title, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 14, 7) }, 0, row);
        control.Margin = new Padding(0, 4, 0, 4); Inputs.Controls.Add(control, 1, row);
    }
    protected ComboBox AddColumn(string title, bool optional = false)
    {
        var column = Choice(); _columns.Add(new ColumnBinding(column, optional)); AddField(title, column); return column;
    }
    protected Button AddAction(string title, Func<Task> action)
    {
        var button = new Button { Text = title, AutoSize = true, MinimumSize = new Size(110, 34), Margin = new Padding(0, 4, 8, 4) };
        button.Click += async (_, _) =>
        {
            if (!button.Enabled || _actionRunning) return; _actionRunning = true; Actions.Enabled = false;
            try { await action(); } catch (Exception error) { Owner.ShowError(error); }
            finally { _actionRunning = false; if (!Actions.IsDisposed) Actions.Enabled = true; }
        };
        Actions.Controls.Add(button); return button;
    }
    protected static DataGridView ResultGrid(params string[] names)
    {
        var grid = new DataGridView { ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, RowHeadersVisible = false, BorderStyle = BorderStyle.None, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, Height = 285, MinimumSize = new Size(0, 180) };
        foreach (var name in names) grid.Columns.Add("c" + grid.Columns.Count, name);
        return grid;
    }
    protected CsvDocument RequireDocument() => Document ?? throw new InvalidOperationException("解析対象の表を選択してください。");
    protected static int RequireColumn(ComboBox column) => column.SelectedIndex >= 0 ? column.SelectedIndex : throw new InvalidOperationException("対象の列を選択してください。");
    protected static int? OptionalColumn(ComboBox column) => column.SelectedIndex > 0 ? column.SelectedIndex - 1 : null;

    internal virtual void RefreshDocuments(CsvDocument? selected = null)
    {
        var previous = Document; selected ??= previous; Refreshing = true;
        try
        {
            Documents.Items.Clear(); foreach (var document in Owner.Workspace.Documents) Documents.Items.Add(new DocumentChoice(document));
            var index = Documents.Items.Cast<DocumentChoice>().ToList().FindIndex(choice => choice.Document == selected);
            if (Documents.Items.Count > 0) Documents.SelectedIndex = Math.Max(0, index);
            RefreshColumns();
        }
        finally { Refreshing = false; }
        if (!ReferenceEquals(previous, Document)) DocumentChanged();
    }

    protected virtual void RefreshColumns()
    {
        foreach (var binding in _columns)
        {
            var choice = binding.Choice; var optional = binding.Optional; var offset = optional ? 1 : 0;
            var selected = choice.SelectedIndex - offset;
            var name = ReferenceEquals(binding.Document, Document) && selected >= 0 && selected < binding.Names.Length ? binding.Names[selected] : null;
            choice.BeginUpdate(); choice.Items.Clear();
            if (optional) choice.Items.Add("（指定しない）");
            if (Document is { } document) foreach (var column in document.Columns) choice.Items.Add(column.HeaderName);
            binding.Document = Document; binding.Names = Document?.Columns.Select(column => column.Name).ToArray() ?? [];
            var index = name is null ? -1 : Array.IndexOf(binding.Names, name);
            if (choice.Items.Count > 0) choice.SelectedIndex = index >= 0 ? index + offset : 0;
            choice.EndUpdate();
        }
    }
    protected virtual void DocumentChanged() { Status.Text = "設定を選び、［実行］を押してください。"; }
    internal virtual void ApplyAppearance() { UiTheme.Apply(this); Status.ForeColor = UiTheme.Muted; }
}

internal sealed class FeaturePanel : Panel
{
    internal FeaturePanel() => DoubleBuffered = true;
}

internal sealed class FeatureTable : TableLayoutPanel
{
    internal FeatureTable() => DoubleBuffered = true;
}

/// <summary>Compact native-keyboard tabs with a buffered, theme-aware header.</summary>
internal sealed class FeatureTabs : TabControl
{
    private bool _fitting;
    internal FeatureTabs()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        DrawMode = TabDrawMode.OwnerDrawFixed; SizeMode = TabSizeMode.Fixed; Padding = new Point(8, 5);
        SizeChanged += (_, _) => FitHeaders(); ControlAdded += (_, _) => FitHeaders(); ControlRemoved += (_, _) => FitHeaders();
        FitHeaders();
    }
    private void FitHeaders()
    {
        if (_fitting || IsDisposed) return;
        _fitting = true;
        try
        {
            var size = new Size(Math.Max(64, Math.Min((int)(110 * DeviceDpi / 96f), (ClientSize.Width - 8) / Math.Max(1, TabCount))), Math.Max(30, Font.Height + 14));
            if (ItemSize != size) ItemSize = size;
        }
        finally { _fitting = false; }
    }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); FitHeaders(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); FitHeaders(); }
    protected override void OnPaintBackground(PaintEventArgs e) { using var brush = new SolidBrush(UiTheme.Bg); e.Graphics.FillRectangle(brush, ClientRectangle); }
    protected override void OnPaint(PaintEventArgs e)
    {
        using var surface = new SolidBrush(UiTheme.Surface); e.Graphics.FillRectangle(surface, DisplayRectangle);
        for (var i = 0; i < TabCount; i++) DrawHeader(e.Graphics, i);
    }
    protected override void OnDrawItem(DrawItemEventArgs e) { if (e.Index >= 0 && e.Index < TabCount) DrawHeader(e.Graphics, e.Index); }
    private void DrawHeader(Graphics graphics, int index)
    {
        var rectangle = GetTabRect(index); var selected = index == SelectedIndex;
        using var background = new SolidBrush(selected ? UiTheme.Surface : UiTheme.Bg); graphics.FillRectangle(background, rectangle);
        if (selected) { using var accent = new SolidBrush(UiTheme.Accent); graphics.FillRectangle(accent, rectangle.Left + 8, rectangle.Bottom - 3, Math.Max(1, rectangle.Width - 16), 2); }
        TextRenderer.DrawText(graphics, TabPages[index].Text, Font, Rectangle.Inflate(rectangle, -7, -3), TabPages[index].Enabled ? selected ? UiTheme.Text : UiTheme.Muted : UiTheme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

internal sealed class LogImportLandingPage : LogFeaturePage
{
    internal LogImportLandingPage(MainForm owner) : base(owner, "アクセスログの読み込み", "ログの出力形式と設定値を指定して読み込みます。読み込みを実行するまで、ファイルの全件処理は開始しません。")
    {
        Inputs.Visible = false;
        AddAction("アクセスログを開く…", () => owner.ImportAccessLogAsync());
        AddContent(new Label { AutoSize = true, MaximumSize = new Size(900, 0), Text = "nginx・Apache・Tomcat・JBoss EAPのテキストアクセスログ、AWS WAF・Cloudflare WAFのJSON Linesを選べます。\nログ設定の文字列とエスケープ方式を合わせ、引用符内の空白を1つの値として読み込みます。\ngzip圧縮されたファイルも、そのまま指定できます。", Margin = new Padding(0, 16, 0, 8) });
    }
}
