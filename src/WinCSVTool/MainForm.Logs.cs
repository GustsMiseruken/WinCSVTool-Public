using WinCSVTool.Core;
using System.Drawing.Drawing2D;

namespace WinCSVTool;

internal sealed partial class MainForm
{
    private TabPage? _logTab;
    private LogAnalysisPage? _logAnalysis;
    private IpAnalysisService? _ipReference;
    internal IpAnalysisService IpReference => _ipReference ??= new IpAnalysisService();

    private void OpenLogAnalysis(string feature = "ip")
    {
        if (_busy) return;
        var selected = Current?.Document;
        if (_logTab is null)
        {
            _logTab = new TabPage("ログ解析") { Tag = "logs", Padding = Padding.Empty, BackColor = UiTheme.Surface };
            _logAnalysis = new LogAnalysisPage(this); _logTab.Controls.Add(_logAnalysis); _tabs.TabPages.Add(_logTab);
        }
        _logAnalysis!.RefreshDocuments(selected); _logAnalysis.SelectFeature(feature); _logAnalysis.ApplyAppearance(); _tabs.SelectedTab = _logTab; UpdateInspector();
    }

    internal async Task<bool> EnrichLogAsync(CsvDocument document, int column, IpEnrichmentFields fields = IpEnrichmentFields.All)
    {
        var selectedCount = System.Numerics.BitOperations.PopCount((uint)(fields & IpEnrichmentFields.All));
        if (selectedCount == 0) return false;
        if (!ConfirmUnsupportedUndo($"全 {document.RowCount:N0} 行へのIP・ASN解析{selectedCount}列の追加")) return false;
        var progress = new Progress<ImportProgress>(p => _state.Text = $"IP解析 — {p.Rows:N0} / {document.RowCount:N0} 行");
        if (!await RunWork("IP・ASN解析列を追加", token => Workspace.EnrichIpAddresses(document, column, IpReference, Settings.CountryLanguage, token, progress, fields))) return false;
        foreach (var page in DocumentPages.Where(p => p.Document == document)) { page.BuildColumns(); page.ApplyAppearance(Settings); await page.ReloadAsync(); page.SetWrap(_wrap); }
        _sqlForm?.RefreshSchema(); return true;
    }
}

internal sealed class IpLogAnalysisPage : UserControl
{
    private sealed record DocumentChoice(CsvDocument Document)
    {
        public override string ToString() => Document.WorkbookSourcePath is { } path ? Path.GetFileName(path) + " / " + Document.WorksheetName : Document.DisplayName;
    }
    private readonly MainForm _owner;
    private readonly ComboBox _documents = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox _columns = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox _address = new() { Dock = DockStyle.Fill, PlaceholderText = "8.8.8.8 / 2001:4860:4860::8888" };
    private readonly TextBox _asn = new() { Dock = DockStyle.Fill, PlaceholderText = "AS15169" };
    private readonly Label _metadata = new() { AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(0, 6, 0, 6) };
    private readonly ToolTip _metadataTip = new() { ShowAlways = true, AutoPopDelay = 15000 };
    private readonly DataGridView _results = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BorderStyle = BorderStyle.None, AllowUserToResizeRows = false };
    private readonly FlowLayoutPanel _fieldChoices = new() { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Margin = Padding.Empty, Padding = new Padding(0, 2, 0, 2) };
    private readonly List<LogColumnCheckBox> _fieldChecks = [];
    private readonly Button _enrich = new() { Text = "選択した列を追加", Dock = DockStyle.Fill, AutoSize = true, MinimumSize = new Size(200, 36), Margin = new Padding(0, 4, 0, 4) };
    private readonly Label _selectionSummary = new() { AutoSize = true, Margin = new Padding(0, 2, 0, 8) };
    internal IpEnrichmentFields SelectedFields => _fieldChecks.Where(c => c.Checked).Aggregate(IpEnrichmentFields.None, (fields, c) => fields | (IpEnrichmentFields)c.Tag!);
    private bool _refreshing;

    internal IpLogAnalysisPage(MainForm owner)
    {
        _owner = owner; Dock = DockStyle.Fill; Padding = new Padding(18);
        var inputs = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, RowCount = 10, Padding = Padding.Empty };
        inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170)); inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        var heading = new Label { Text = "ログ解析 — IP・ASN", AutoSize = true, Font = new Font("Yu Gothic UI", 14, FontStyle.Bold), Margin = new Padding(0, 0, 0, 8) };
        inputs.Controls.Add(heading, 0, 0); inputs.SetColumnSpan(heading, 3);
        var description = new Label { Text = "同梱の参照データで国・地域とネットワーク事業者を推定します。通信は行いません。", AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
        inputs.Controls.Add(description, 0, 1); inputs.SetColumnSpan(description, 3);
        AddInput(inputs, 2, "IPアドレス", _address, "IPを調べる", LookupIpAsync);
        AddInput(inputs, 3, "AS番号", _asn, "ASNを調べる", LookupAsnAsync);
        AddInput(inputs, 4, "解析対象の表", _documents, "表の一覧を更新", () => { RefreshDocuments(); return Task.CompletedTask; });
        AddInput(inputs, 5, "IPアドレスの列", _columns, "選択した列を追加", EnrichAsync, _enrich);
        foreach (var (field, label, explanation) in new[]
        {
            (IpEnrichmentFields.Country, "国・地域", "IPアドレスに対応する国・地域の推定名です。環境設定で日本語または英語を選べます。利用者の国籍や実際の所在地を確定する情報ではありません。"),
            (IpEnrichmentFields.CountryCode, "国コード", "国・地域の2文字コードです。例：日本はJP、米国はUS。プライベートIPなど、国を割り当てないアドレスは空欄になります。"),
            (IpEnrichmentFields.AddressClass, "IP区分", "公開IP、プライベートIP、ループバックなどのアドレス区分です。国・地域や事業者の判定対象にならない特殊用途のIPも区別できます。"),
            (IpEnrichmentFields.Asn, "ASN", "ネットワーク運用主体を識別するAS番号です。例：15169。ネットワークに関する参考情報で、個人の契約先を示すものではありません。"),
            (IpEnrichmentFields.Organization, "事業者", "ASNに記録された組織・ネットワーク事業者の名称です。利用者の契約先や法的な所有者と一致するとは限りません。"),
            (IpEnrichmentFields.SnapshotDate, "基準日", "解析に使用した同梱参照データの基準日です。参照データの時点を確認できます。インターネットによる自動更新は行いません。"),
            (IpEnrichmentFields.Source, "出典", "解析に使用した参照情報の提供元です。DB-IPや特殊用途アドレスのIANA情報など、結果の情報元を記録します。"),
            (IpEnrichmentFields.License, "ライセンス", "参照データのライセンスと帰属表示に関する情報です。国・地域やASNの結果に使用したデータの利用条件を記録します。")
        })
        {
            var option = new LogFieldOption(field, label, explanation);
            option.Check.CheckedChanged += (_, _) => UpdateFieldSelection();
            _fieldChecks.Add(option.Check); _fieldChoices.Controls.Add(option);
        }
        inputs.Controls.Add(new Label { Text = "追加する列", AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(0, 9, 12, 6) }, 0, 6);
        inputs.Controls.Add(_fieldChoices, 1, 6); inputs.SetColumnSpan(_fieldChoices, 2);
        inputs.Controls.Add(_selectionSummary, 1, 7); inputs.SetColumnSpan(_selectionSummary, 2);
        var referenceRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
        referenceRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); referenceRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        referenceRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var referenceDetails = new Button { Text = "参照データの詳細…", AutoSize = false, Anchor = AnchorStyles.Left | AnchorStyles.Right, Size = new Size(230, 36), MinimumSize = new Size(200, 36), Margin = new Padding(0, 6, 0, 6) };
        referenceDetails.Click += (_, _) => ShowReferenceDetails();
        referenceRow.Controls.Add(_metadata, 0, 0); referenceRow.Controls.Add(referenceDetails, 1, 0);
        inputs.Controls.Add(referenceRow, 0, 8); inputs.SetColumnSpan(referenceRow, 3);
        var note = new Label { Dock = DockStyle.Bottom, Height = 38, Padding = new Padding(0, 8, 0, 0), Text = "チェックした列だけを、元の表の全行に追加します。参照データの基準日・出典・ライセンスは［参照データの詳細］でいつでも確認できます。", AutoEllipsis = true };
        _results.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "項目", FillWeight = 28, SortMode = DataGridViewColumnSortMode.NotSortable });
        _results.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "参照結果", FillWeight = 72, SortMode = DataGridViewColumnSortMode.NotSortable });
        _results.RowTemplate.Height = 31;
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var content = new Panel { Location = Point.Empty };
        content.Controls.Add(_results); content.Controls.Add(note); content.Controls.Add(inputs);
        scroll.Controls.Add(content); Controls.Add(scroll);
        var arranging = false;
        void ArrangeContent()
        {
            if (arranging || scroll.IsDisposed) return;
            arranging = true;
            try
            {
                // A constrained width lets the horizontal choices wrap. Keep a usable
                // results area and scroll the whole page when vertical space is limited.
                content.Width = Math.Max(1, scroll.ClientSize.Width - SystemInformation.VerticalScrollBarWidth);
                content.PerformLayout();
                var minimumResultsHeight = Math.Max((int)Math.Round(180 * DeviceDpi / 96f), Font.Height * 8);
                content.Height = Math.Max(scroll.ClientSize.Height, inputs.Height + note.Height + minimumResultsHeight);
                scroll.AutoScrollMinSize = new Size(0, content.Height);
                scroll.PerformLayout();
            }
            finally { arranging = false; }
        }
        scroll.SizeChanged += (_, _) => ArrangeContent();
        inputs.SizeChanged += (_, _) => ArrangeContent();
        FontChanged += (_, _) => ArrangeContent();
        HandleCreated += (_, _) => ArrangeContent();
        ArrangeContent();
        _documents.SelectedIndexChanged += (_, _) => { if (!_refreshing) RefreshColumns(); };
        _columns.SelectedIndexChanged += (_, _) => UpdateFieldSelection();
        UpdateFieldSelection();
        ApplyAppearance();
    }

    private static void AddInput(TableLayoutPanel panel, int row, string label, Control input, string buttonText, Func<Task> action, Button? suppliedButton = null)
    {
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 6) }, 0, row);
        input.Margin = new Padding(0, 6, 12, 6); panel.Controls.Add(input, 1, row);
        var button = suppliedButton ?? new Button { Text = buttonText, Dock = DockStyle.Fill, AutoSize = true, MinimumSize = new Size(200, 36), Margin = new Padding(0, 4, 0, 4) };
        button.Click += async (_, _) => await action(); panel.Controls.Add(button, 2, row);
    }

    internal void RefreshDocuments(CsvDocument? selected = null)
    {
        selected ??= (_documents.SelectedItem as DocumentChoice)?.Document;
        _refreshing = true;
        try
        {
            _documents.Items.Clear();
            foreach (var document in _owner.Workspace.Documents) _documents.Items.Add(new DocumentChoice(document));
            var index = _documents.Items.Cast<DocumentChoice>().ToList().FindIndex(choice => choice.Document == selected);
            if (_documents.Items.Count > 0) _documents.SelectedIndex = Math.Max(0, index);
        }
        finally { _refreshing = false; }
        RefreshColumns();
    }

    private void RefreshColumns()
    {
        _columns.Items.Clear();
        if (_documents.SelectedItem is not DocumentChoice choice) return;
        foreach (var column in choice.Document.Columns) _columns.Items.Add(column.HeaderName);
        var guessed = choice.Document.Columns.FindIndex(c => c.HeaderName.Contains("IP", StringComparison.OrdinalIgnoreCase) || c.HeaderName.Contains("アドレス", StringComparison.Ordinal));
        if (_columns.Items.Count > 0) _columns.SelectedIndex = Math.Max(0, guessed);
        UpdateFieldSelection();
    }

    private void UpdateFieldSelection()
    {
        var count = _fieldChecks.Count(check => check.Checked);
        _enrich.Enabled = count > 0 && _documents.SelectedItem is DocumentChoice && _columns.SelectedIndex >= 0;
        _selectionSummary.Text = count == 0 ? "追加する列を1つ以上選択してください。" : $"{count}列を追加 · 元のIP列と他の値は保持します";
        _selectionSummary.ForeColor = UiTheme.Muted;
    }

    internal void ApplyAppearance()
    {
        UiTheme.Apply(this); UiTheme.StyleGrid(_results); _results.GridColor = UiTheme.Border; _metadata.ForeColor = UiTheme.Muted;
        try
        {
            var data = _owner.IpReference.Metadata;
            _metadata.Text = $"基準日: {data.SnapshotDate}  ·  DB-IP Lite  ·  CC BY 4.0\n国・地域 / ASN: DB-IP  ·  特殊用途アドレス: IANA";
            _metadataTip.SetToolTip(_metadata, IpReferenceInfoForm.Describe(data));
        }
        catch (Exception ex) { _metadata.Text = "参照データを利用できません: " + ex.Message; }
    }

    private void ShowReferenceDetails()
    {
        try { using var details = new IpReferenceInfoForm(_owner.IpReference.Metadata); details.ShowDialog(_owner); }
        catch (Exception ex) { _owner.ShowError(ex); }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _metadataTip.Dispose();
    }

    private void ShowResult(IEnumerable<(string Name, string Value)> values)
    {
        _results.Rows.Clear(); foreach (var (name, value) in values) _results.Rows.Add(name, string.IsNullOrEmpty(value) ? "不明 / 該当なし" : value);
    }
    private async Task LookupIpAsync()
    {
        IpLookupResult? result = null;
        var address = _address.Text; var language = _owner.Settings.CountryLanguage;
        if (!await _owner.RunWork("IPアドレスを解析", _ => result = _owner.IpReference.Lookup(address, language))) return;
        ShowResult([("IPアドレス", result!.Address), ("国・地域（参考）", result.CountryName), ("国コード", result.CountryCode), ("IP区分", result.AddressClass), ("ASN", result.Asn is { } number ? "AS" + number : ""), ("AS組織・事業者（参考）", result.Organization), ("データ基準日", result.SnapshotDate), ("出典", result.Source), ("ライセンス", result.License)]);
    }
    private async Task LookupAsnAsync()
    {
        AsnLookupResult? result = null;
        var asn = _asn.Text;
        if (!await _owner.RunWork("ASNを解析", _ => result = _owner.IpReference.LookupAsn(asn))) return;
        ShowResult([("ASN", result!.Asn is { } number ? "AS" + number : ""), ("AS組織・事業者（参考）", result.Organization), ("データ基準日", result.SnapshotDate), ("出典", result.Source), ("ライセンス", result.License)]);
    }
    private async Task EnrichAsync()
    {
        if (_documents.SelectedItem is not DocumentChoice choice || _columns.SelectedIndex < 0) return;
        var fields = SelectedFields;
        if (fields == IpEnrichmentFields.None) return;
        var count = _fieldChecks.Count(check => check.Checked);
        if (await _owner.EnrichLogAsync(choice.Document, _columns.SelectedIndex, fields)) { RefreshColumns(); ShowResult([("処理結果", choice.Document.RowCount.ToString("N0") + $" 行に選択した{count}列を追加しました。"), ("データ基準日", _owner.IpReference.Metadata.SnapshotDate), ("出典", _owner.IpReference.Metadata.Source)]); }
    }
}

/// <summary>Each option has one text owner, an independent indicator, and click-only help.</summary>
internal sealed class LogFieldOption : Panel
{
    internal LogColumnCheckBox Check { get; }
    private readonly Label _label;
    private readonly LogFieldInfoButton _info;
    private readonly string _explanation;
    private ToolStripDropDown? _popup;

    internal LogFieldOption(IpEnrichmentFields field, string name, string explanation)
    {
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; BorderStyle = BorderStyle.None;
        Margin = new Padding(0, 4, 15, 4); Padding = Padding.Empty; BackColor = UiTheme.Surface;
        _explanation = explanation;
        Check = new LogColumnCheckBox { Tag = field, Checked = true, AccessibleName = name, AccessibleDescription = name + "列を追加する", TabIndex = 0 };
        _label = new Label { Text = name, AutoSize = true, TabStop = false, Margin = Padding.Empty, BackColor = Color.Transparent };
        _label.Click += (_, _) => { if (Check.Enabled) { Check.Focus(); Check.Checked = !Check.Checked; } };
        _info = new LogFieldInfoButton { AccessibleName = name + "の説明", AccessibleDescription = "クリックすると列の説明を表示します", TabIndex = 1 };
        _info.Click += (_, _) => ShowExplanation();
        Controls.Add(Check); Controls.Add(_label); Controls.Add(_info);
    }

    private int Metric(int value) => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96f));

    public override Size GetPreferredSize(Size proposedSize)
    {
        if (_label is null) return base.GetPreferredSize(proposedSize);
        var check = Check.GetPreferredSize(Size.Empty); var label = _label.PreferredSize; var info = _info.GetPreferredSize(Size.Empty);
        return new Size(check.Width + Metric(5) + label.Width + Metric(4) + info.Width, Math.Max(check.Height, Math.Max(label.Height, info.Height)));
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_label is null) return;
        var check = Check.GetPreferredSize(Size.Empty); var label = _label.PreferredSize; var info = _info.GetPreferredSize(Size.Empty);
        Check.Bounds = new Rectangle(0, Math.Max(0, (Height - check.Height) / 2), check.Width, check.Height);
        _label.Bounds = new Rectangle(Check.Right + Metric(5), Math.Max(0, (Height - label.Height) / 2), label.Width, label.Height);
        _info.Bounds = new Rectangle(_label.Right + Metric(4), Math.Max(0, (Height - info.Height) / 2), info.Width, info.Height);
    }

    private void ShowExplanation()
    {
        if (_popup?.Visible == true) { _popup.Close(); return; }
        _popup?.Dispose();
        var title = new Label { Text = _label.Text, ForeColor = UiTheme.Text, Font = Font, Margin = Padding.Empty, UseMnemonic = false };
        var text = new Label { Text = _explanation, Font = Font, ForeColor = UiTheme.Text, Margin = Padding.Empty, UseMnemonic = false };
        var body = new Panel { BackColor = UiTheme.Surface, Margin = Padding.Empty };
        body.Controls.Add(title); body.Controls.Add(text);
        var host = new ToolStripControlHost(body) { AutoSize = false, Margin = Padding.Empty, Padding = Padding.Empty };
        _popup = new ToolStripDropDown { AutoClose = true, Padding = new Padding(1), ShowItemToolTips = false, DropShadowEnabled = true };
        _popup.Items.Add(host); UiTheme.ApplyToolStrip(_popup);
        var popup = _popup;
        void SizeExplanation()
        {
            var scale = popup.DeviceDpi / 96f;
            var padding = Math.Max(10, (int)Math.Round(14 * scale));
            var area = Screen.FromControl(_info).WorkingArea;
            var width = Math.Min((int)Math.Round(320 * scale), area.Width - 2);
            var textWidth = Math.Max(1, width - padding * 2);
            using var graphics = text.CreateGraphics();
            var heading = TextRenderer.MeasureText(graphics, title.Text, title.Font, new Size(textWidth, int.MaxValue), TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            var paragraph = TextRenderer.MeasureText(graphics, text.Text, text.Font, new Size(textWidth, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            title.Bounds = new Rectangle(padding, padding, textWidth, heading.Height + 2);
            text.Bounds = new Rectangle(padding, title.Bottom + (int)Math.Round(8 * scale), textWidth, paragraph.Height + Math.Max(2, (int)Math.Ceiling(3 * scale)));
            body.Size = new Size(width, text.Bottom + padding);
            host.Size = body.Size; popup.PerformLayout();
            if (popup.Visible) popup.Location = new Point(Math.Clamp(popup.Left, area.Left, Math.Max(area.Left, area.Right - popup.Width)), Math.Clamp(popup.Top, area.Top, Math.Max(area.Top, area.Bottom - popup.Height)));
        }
        SizeExplanation();
        popup.Opened += (_, _) => SizeExplanation();
        _popup.Show(_info, new Point(0, _info.Height + Metric(4)));
    }

    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); PerformLayout(); Parent?.PerformLayout(); }
    protected override void Dispose(bool disposing) { if (disposing) _popup?.Dispose(); base.Dispose(disposing); }
}

/// <summary>The checkbox owns only its indicator; its caption belongs to a normal Label.</summary>
internal sealed class LogColumnCheckBox : CheckBox, IThemeOwnedControl
{
    internal LogColumnCheckBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
        AutoSize = true; Text = ""; FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
        Margin = Padding.Empty; Padding = Padding.Empty; UseVisualStyleBackColor = false; ApplyTheme();
    }

    private int Metric(int value) => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96f));
    public override Size GetPreferredSize(Size proposedSize) => new(Metric(22), Metric(22));
    public void ApplyTheme() { BackColor = UiTheme.Surface; ForeColor = UiTheme.Accent; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var background = new SolidBrush(UiTheme.Surface);
        e.Graphics.FillRectangle(background, ClientRectangle);
        var size = Metric(16);
        var box = new Rectangle((Width - size) / 2, (Height - size) / 2, size, size);
        var radius = Metric(4);
        using var path = new GraphicsPath();
        path.AddArc(box.Left, box.Top, radius * 2, radius * 2, 180, 90);
        path.AddArc(box.Right - radius * 2 - 1, box.Top, radius * 2, radius * 2, 270, 90);
        path.AddArc(box.Right - radius * 2 - 1, box.Bottom - radius * 2 - 1, radius * 2, radius * 2, 0, 90);
        path.AddArc(box.Left, box.Bottom - radius * 2 - 1, radius * 2, radius * 2, 90, 90); path.CloseFigure();
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var fill = new SolidBrush(Color.FromArgb(Checked && Enabled ? 48 : 14, UiTheme.Accent));
        using var border = new Pen(Checked && Enabled ? Color.FromArgb(135, UiTheme.Accent) : UiTheme.Border, Math.Max(1, DeviceDpi / 96f));
        e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(border, path);
        if (Checked)
        {
            using var tick = new Pen(Enabled ? UiTheme.Accent : UiTheme.Muted, Math.Max(1.8f, 1.8f * DeviceDpi / 96f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            e.Graphics.DrawLines(tick, [new PointF(box.Left + size * .24f, box.Top + size * .51f), new PointF(box.Left + size * .44f, box.Top + size * .71f), new PointF(box.Left + size * .77f, box.Top + size * .31f)]);
        }
        if (Focused && ShowFocusCues) { using var focus = new Pen(UiTheme.Accent, 1) { DashStyle = DashStyle.Dot }; e.Graphics.DrawRectangle(focus, Rectangle.Inflate(box, 2, 2)); }
    }

    protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
}

internal sealed class LogFieldInfoButton : Button, IThemeOwnedControl
{
    internal LogFieldInfoButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
        AutoSize = true; Text = ""; FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; Margin = Padding.Empty;
        Cursor = Cursors.Hand; ApplyTheme();
    }

    public void ApplyTheme() { BackColor = UiTheme.Surface; ForeColor = UiTheme.Muted; Invalidate(); }
    public override Size GetPreferredSize(Size proposedSize) { var size = Math.Max(18, (int)Math.Round(20 * DeviceDpi / 96f)); return new Size(size, size); }
    protected override void OnPaint(PaintEventArgs e)
    {
        using var background = new SolidBrush(UiTheme.Surface);
        e.Graphics.FillRectangle(background, ClientRectangle);
        var inset = Math.Max(2, (int)Math.Round(3 * DeviceDpi / 96f));
        var bounds = Rectangle.Inflate(ClientRectangle, -inset, -inset); bounds.Width--; bounds.Height--;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Focused && ShowFocusCues ? UiTheme.Accent : ForeColor, Math.Max(1, DeviceDpi / 96f));
        e.Graphics.DrawEllipse(pen, bounds);
        TextRenderer.DrawText(e.Graphics, "i", Font, bounds, ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.PreserveGraphicsClipping);
    }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
}
