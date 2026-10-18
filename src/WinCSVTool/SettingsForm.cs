namespace WinCSVTool;

internal sealed class SettingsForm : Form
{
    private readonly AppSettings _original;
    private readonly ListBox _navigation = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, IntegralHeight = false, ItemHeight = 38, DrawMode = DrawMode.OwnerDrawFixed };
    private readonly Panel _content = new() { Dock = DockStyle.Fill, Padding = new Padding(25, 16, 25, 16) };
    private readonly List<Control> _pages = [];
    private readonly TextBox _workFolder = new() { Dock = DockStyle.Fill, PlaceholderText = "空欄の場合はWindowsの一時フォルダー" };
    private readonly ComboBox _theme = Choices("ライト", "ダーク", "システム設定に合わせる");
    private readonly ComboBox _countryLanguage = Choices("日本語", "English");
    private readonly CheckBox _ctrlMouseWheelZoom = Check("Ctrl + マウスホイールで表示倍率を変更する");
    private readonly NumericUpDown _pageSize = Number(100, 10_000, 512, 0, 128);
    private readonly ComboBox _readEncoding = Choices("自動判定", "UTF-8", "Shift_JIS (CP932)", "EUC-JP", "UTF-16 LE", "UTF-16 BE");
    private readonly ComboBox _saveEncoding = Choices("UTF-8", "Shift_JIS (CP932)", "EUC-JP", "UTF-16 LE", "UTF-16 BE");
    private readonly ComboBox _delimiter = Choices("カンマ (,)", "タブ (TSV)", "セミコロン (;)");
    private readonly CheckBox _hasHeader = Check("先頭レコードをヘッダーとして扱う");
    private readonly CheckBox _confirmRead = Check("ファイルを開くたびに読み込み設定を確認する");
    private readonly CheckBox _writeBom = Check("保存時にBOMを付ける");
    private readonly ComboBox _sqlLayout = Choices("別ウィンドウ", "CSVの右側に並べる", "CSVの下側に並べる", "同じウィンドウのタブ");
    private readonly CheckBox _rememberWindow = Check("ウィンドウの位置とサイズを記憶する");
    private readonly CheckBox _startMaximized = Check("起動時にウィンドウを最大化する");
    private readonly CheckBox _inspectorVisible = Check("セル値の編集パネルを表示する");
    private readonly NumericUpDown _zoom = Number(50, 200, 100, 0, 10);
    private readonly NumericUpDown _gridSize = Number(6, 32, 9.5m, 1, 0.5m);
    private readonly NumericUpDown _editorSize = Number(6, 32, 11, 1, 0.5m);
    private readonly Label _gridFontName = new() { AutoSize = true };
    private readonly Label _editorFontName = new() { AutoSize = true };
    private readonly Label _gridSample = new() { AutoSize = false, Height = 44, Text = "セルの表示例  000123  日本語 ABC", Padding = new Padding(8) };
    private readonly Label _editorSample = new() { AutoSize = false, Height = 44, Text = "SELECT * FROM csv_1;", Padding = new Padding(8) };
    private string _gridFamily = "Yu Gothic UI";
    private string _editorFamily = "Consolas";
    private readonly DataGridView _shortcuts = new()
    {
        Dock = DockStyle.Fill, Height = 350, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, MultiSelect = false, ReadOnly = true, RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        BorderStyle = BorderStyle.None, BackgroundColor = Color.White, RowTemplate = { Height = 30 }
    };
    private readonly Label _shortcutHint = new() { AutoSize = true, Text = "操作を選択し、割り当てたいキーを押してください。" };
    private readonly Dictionary<string, string> _shortcutValues;
    private static readonly string[] Encodings = ["utf-8", "shift_jis", "euc-jp", "utf-16", "utf-16BE"];

    public AppSettings Result { get; private set; }

    public SettingsForm(AppSettings current)
    {
        _original = current.Clone();
        _original.Normalize();
        Result = _original.Clone();
        _shortcutValues = new(_original.Shortcuts, StringComparer.Ordinal);
        Text = "環境設定 — WinCSVTool";
        ClientSize = new Size(900, 690);
        MinimumSize = new Size(790, 590);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;

        var heading = new TableLayoutPanel { Dock = DockStyle.Top, Height = 92, Padding = new Padding(24, 17, 24, 12), ColumnCount = 1, RowCount = 2 };
        heading.Controls.Add(new Label { Text = "環境設定", AutoSize = true, Font = new Font("Yu Gothic UI", 17, FontStyle.Bold) });
        heading.Controls.Add(new Label { Text = "表示や操作を、お使いの環境に合わせて調整できます。", AutoSize = true });

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 175));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var navPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 16, 8, 10) };
        navPanel.Controls.Add(_navigation);
        body.Controls.Add(navPanel, 0, 0);
        body.Controls.Add(_content, 1, 0);
        Controls.Add(body);
        Controls.Add(heading);
        AddFooter();

        AddPage("一般", BuildGeneral());
        AddPage("フォントと表示", BuildDisplay());
        AddPage("読み込み・保存", BuildCsv());
        AddPage("ウィンドウ配置", BuildWindow());
        AddPage("ショートカット", BuildShortcuts());
        _navigation.SelectedIndexChanged += (_, _) => SelectPage();
        _navigation.DrawItem += DrawNavigation;
        LoadValues();
        UiTheme.ThemeForm(this);
        heading.BackColor = UiTheme.Header;
        navPanel.BackColor = UiTheme.Header;
        _navigation.BackColor = UiTheme.Header;
        _shortcuts.EnableHeadersVisualStyles = false;
        _shortcuts.BackgroundColor = UiTheme.Surface;
        _shortcuts.GridColor = UiTheme.GridLine;
        _shortcuts.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _shortcuts.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        _shortcuts.ColumnHeadersDefaultCellStyle.BackColor = UiTheme.Header;
        _shortcuts.ColumnHeadersDefaultCellStyle.ForeColor = UiTheme.Text;
        _shortcuts.DefaultCellStyle.BackColor = UiTheme.Surface;
        _shortcuts.DefaultCellStyle.ForeColor = UiTheme.Text;
        _shortcuts.DefaultCellStyle.SelectionBackColor = UiTheme.Selection;
        _shortcuts.DefaultCellStyle.SelectionForeColor = UiTheme.Text;
        _workFolder.BorderStyle = BorderStyle.None;
        _navigation.SelectedIndex = 0;
    }

    private void AddFooter()
    {
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 76, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(18, 14, 18, 14), WrapContents = false
        };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(134, 42), Margin = new Padding(8, 0, 0, 0) };
        var ok = new Button { Text = "設定を保存", AutoSize = true, MinimumSize = new Size(134, 42), Margin = new Padding(8, 0, 0, 0) };
        ok.Click += (_, _) => ApplyValues();
        footer.Controls.AddRange([cancel, ok]);
        Controls.Add(footer);
        AcceptButton = ok;
        CancelButton = cancel;
        UiTheme.StyleButton(ok, true);
        // ThemeForm marks primary buttons using DialogResult; validation must run before the form closes.
        Shown += (_, _) => UiTheme.StyleButton(ok, true);
    }

    private Control BuildGeneral()
    {
        var page = Page("一般", "大きなCSVを処理するための作業データとページ表示を設定します。");
        page.Controls.Add(Field("作業データの保存先", FolderPicker()));
        page.Controls.Add(Hint("空欄の場合はWindowsの一時フォルダーを使用します。大きなCSVには十分な空き容量が必要です。変更は次回起動時から有効です。"));
        page.Controls.Add(Field("1ページの最大表示行数", _pageSize));
        page.Controls.Add(Hint("CSV全体の行数制限ではありません。ページを切り替えて全行を参照できます。値を大きくすると表示時のメモリ使用量が増えます。推奨: 512行。"));
        page.Controls.Add(Field("ログ解析の国・地域名の表示言語", _countryLanguage));
        return page;
    }

    private Control FolderPicker()
    {
        var line = new TableLayoutPanel { Height = 42, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
        line.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        line.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        line.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        line.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var browse = new Button { Text = "参照…", AutoSize = false, Width = 104, MinimumSize = new Size(104, 42), Dock = DockStyle.Fill, Margin = new Padding(8, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "作業データの保存先を選択", UseDescriptionForTitle = true, SelectedPath = Directory.Exists(_workFolder.Text) ? _workFolder.Text : "" };
            if (dialog.ShowDialog(this) == DialogResult.OK) _workFolder.Text = dialog.SelectedPath;
        };
        var clear = new Button { Text = "既定", AutoSize = false, Width = 84, MinimumSize = new Size(84, 42), Dock = DockStyle.Fill, Margin = new Padding(8, 0, 0, 0) };
        clear.Click += (_, _) => _workFolder.Clear();
        var folderFrame = new Panel { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, Margin = Padding.Empty };
        _workFolder.Dock = DockStyle.None;
        _workFolder.BorderStyle = BorderStyle.None;
        _workFolder.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        folderFrame.Controls.Add(_workFolder);
        void AlignFolderText()
        {
            _workFolder.SetBounds(8, Math.Max(0, (folderFrame.ClientSize.Height - _workFolder.PreferredHeight) / 2),
                Math.Max(1, folderFrame.ClientSize.Width - 16), _workFolder.PreferredHeight);
        }
        folderFrame.SizeChanged += (_, _) => AlignFolderText();
        _workFolder.FontChanged += (_, _) => AlignFolderText();
        line.Controls.Add(folderFrame, 0, 0);
        line.Controls.Add(browse, 1, 0);
        line.Controls.Add(clear, 2, 0);
        return line;
    }

    private Control BuildDisplay()
    {
        var page = Page("フォントと表示", "CSVの文字、配色、表示倍率を調整します。");
        page.Controls.Add(Field("カラーテーマ", _theme));
        page.Controls.Add(Field("表示倍率 (%)", _zoom));
        page.Controls.Add(Hint("画面右下の表示倍率コントロールからも変更できます。"));
        page.Controls.Add(_ctrlMouseWheelZoom);
        page.Controls.Add(FontField("CSVセルのフォント", _gridFontName, _gridSize, true));
        page.Controls.Add(_gridSample);
        page.Controls.Add(FontField("セル編集・SQLのフォント", _editorFontName, _editorSize, false));
        page.Controls.Add(_editorSample);
        _gridSize.ValueChanged += (_, _) => RefreshFontSamples();
        _editorSize.ValueChanged += (_, _) => RefreshFontSamples();
        return page;
    }

    private Control FontField(string title, Label name, NumericUpDown size, bool grid)
    {
        var row = new TableLayoutPanel { ColumnCount = 3, RowCount = 1, Height = 44, Padding = Padding.Empty, Margin = Padding.Empty };
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        name.Dock = DockStyle.Fill;
        name.TextAlign = ContentAlignment.MiddleLeft;
        size.Dock = DockStyle.None;
        size.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        size.Margin = new Padding(4, 0, 4, 0);
        var choose = new Button { Text = "変更…", AutoSize = true, MinimumSize = new Size(110, 42), Dock = DockStyle.Fill, Margin = new Padding(8, 0, 0, 0) };
        choose.Click += (_, _) => ChooseFont(grid);
        row.Controls.Add(name, 0, 0);
        row.Controls.Add(size, 1, 0);
        row.Controls.Add(choose, 2, 0);
        return Field(title + " (pt)", row);
    }

    private void ChooseFont(bool grid)
    {
        using var current = new Font(grid ? _gridFamily : _editorFamily, (float)(grid ? _gridSize.Value : _editorSize.Value));
        using var dialog = new FontDialog { Font = current, MinSize = 6, MaxSize = 32, FontMustExist = true, ShowEffects = false, AllowScriptChange = false, AllowVerticalFonts = false };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (grid) { _gridFamily = dialog.Font.Name; _gridSize.Value = Math.Clamp((decimal)dialog.Font.SizeInPoints, 6, 32); }
        else { _editorFamily = dialog.Font.Name; _editorSize.Value = Math.Clamp((decimal)dialog.Font.SizeInPoints, 6, 32); }
        RefreshFontSamples();
    }

    private void RefreshFontSamples()
    {
        _gridFontName.Text = _gridFamily;
        _editorFontName.Text = _editorFamily;
        void SetFont(Label sample, string family, float size)
        {
            var candidate = new Font(family, size, FontStyle.Regular);
            if (sample.Font.Equals(candidate)) candidate.Dispose();
            else
            {
                var previousOwned = sample.Tag as Font;
                sample.Font = candidate;
                sample.Tag = candidate;
                previousOwned?.Dispose();
            }
            sample.Height = Math.Max(44, (int)Math.Ceiling(sample.Font.GetHeight()) + sample.Padding.Vertical + 8);
        }
        SetFont(_gridSample, _gridFamily, (float)_gridSize.Value);
        SetFont(_editorSample, _editorFamily, (float)_editorSize.Value);
    }

    private Control BuildCsv()
    {
        var page = Page("CSVの読み込み・保存", "新しく開くファイルと保存時の既定値を設定します。");
        page.Controls.Add(Field("読み込み時の文字コード", _readEncoding));
        page.Controls.Add(Field("区切り文字", _delimiter));
        page.Controls.Add(_hasHeader);
        page.Controls.Add(_confirmRead);
        page.Controls.Add(Hint("文字コードはファイルごとに変更できます。セル値は文字列のまま保持し、先頭の0を削除しません。"));
        page.Controls.Add(Field("保存時の文字コード", _saveEncoding));
        page.Controls.Add(_writeBom);
        page.Controls.Add(Hint("保存ダイアログで、ファイルごとに文字コード・ヘッダー・BOMを確認できます。"));
        return page;
    }

    private Control BuildWindow()
    {
        var page = Page("ウィンドウとパネル", "CSVとSQL分析の配置を選択します。");
        page.Controls.Add(Field("SQL分析の表示場所", _sqlLayout));
        page.Controls.Add(Hint("「右側」「下側」「タブ」では、CSVと同じウィンドウ内でSQL分析を操作できます。別ウィンドウは他のモニターにも移動できます。"));
        page.Controls.Add(_inspectorVisible);
        page.Controls.Add(_rememberWindow);
        page.Controls.Add(_startMaximized);
        page.Controls.Add(Hint("起動時の最大化は、記憶したウィンドウの状態より優先します。"));
        return page;
    }

    private Control BuildShortcuts()
    {
        var page = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = Padding.Empty, Padding = Padding.Empty };
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.Controls.Add(Heading("キーボードショートカット"), 0, 0);
        page.Controls.Add(Hint("CtrlまたはAltとの組み合わせ、およびF1～F24を割り当てられます。コピーや貼り付けなどの標準編集キーは予約されています。"), 0, 1);
        _shortcuts.Columns.Add(new DataGridViewTextBoxColumn { Name = "Command", HeaderText = "操作", FillWeight = 65, SortMode = DataGridViewColumnSortMode.NotSortable });
        _shortcuts.Columns.Add(new DataGridViewTextBoxColumn { Name = "Shortcut", HeaderText = "ショートカット", FillWeight = 35, SortMode = DataGridViewColumnSortMode.NotSortable });
        foreach (var definition in ShortcutCatalog.Definitions)
        {
            var index = _shortcuts.Rows.Add(definition.Label, "");
            _shortcuts.Rows[index].Tag = definition.Id;
        }
        page.Controls.Add(_shortcuts, 0, 2);
        _shortcutHint.MaximumSize = new Size(520, 0);
        _shortcutHint.Margin = new Padding(0, 12, 0, 10);
        page.Controls.Add(_shortcutHint, 0, 3);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        var clear = new Button { Text = "選択した割り当てを解除", AutoSize = true, Height = 34 };
        clear.Click += (_, _) => { if (_shortcuts.CurrentRow?.Tag is string id) { _shortcutValues[id] = ""; RefreshShortcuts(); } };
        var restore = new Button { Text = "既定に戻す", AutoSize = true, Height = 34 };
        restore.Click += (_, _) => { _shortcutValues.Clear(); foreach (var item in ShortcutCatalog.CreateDefaults()) _shortcutValues.Add(item.Key, item.Value); RefreshShortcuts(); };
        buttons.Controls.AddRange([clear, restore]);
        page.Controls.Add(buttons, 0, 4);
        return page;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_shortcuts.ContainsFocus && _shortcuts.CurrentRow?.Tag is string id)
        {
            var key = keyData & Keys.KeyCode;
            if (key is Keys.Tab or Keys.Escape or Keys.Up or Keys.Down or Keys.ControlKey or Keys.ShiftKey or Keys.Menu)
                return base.ProcessCmdKey(ref msg, keyData);
            if (!ShortcutCatalog.IsAssignable(keyData))
            {
                _shortcutHint.Text = "このキーは割り当てできません。Ctrl・Altとの組み合わせ、またはファンクションキーを指定してください。";
                return true;
            }
            var duplicate = ShortcutCatalog.Definitions.FirstOrDefault(x => x.Id != id && ShortcutCatalog.TryParse(_shortcutValues[x.Id], out var assigned) && assigned == keyData);
            if (duplicate is not null)
            {
                _shortcutHint.Text = $"{DisplayShortcut(keyData)} は「{duplicate.Label}」に割り当て済みです。先にその割り当てを解除してください。";
                return true;
            }
            _shortcutValues[id] = ShortcutCatalog.Format(keyData);
            RefreshShortcuts();
            _shortcutHint.Text = $"「{ShortcutCatalog.Definitions.First(x => x.Id == id).Label}」に {DisplayShortcut(keyData)} を割り当てました。";
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void RefreshShortcuts()
    {
        foreach (DataGridViewRow row in _shortcuts.Rows)
        {
            var value = _shortcutValues[(string)row.Tag!];
            row.Cells[1].Value = ShortcutCatalog.TryParse(value, out var keys) ? DisplayShortcut(keys) : value;
        }
    }

    private static string DisplayShortcut(Keys keys)
    {
        if (keys == Keys.None) return "未設定";
        var key = keys & Keys.KeyCode;
        var text = key switch { Keys.Oemcomma => ",", Keys.Oemplus => "+", Keys.OemMinus => "−", Keys.D0 => "0", _ => key.ToString() };
        return ((keys & Keys.Control) != 0 ? "Ctrl+" : "") + ((keys & Keys.Alt) != 0 ? "Alt+" : "") + ((keys & Keys.Shift) != 0 ? "Shift+" : "") + text;
    }

    private void LoadValues()
    {
        _workFolder.Text = _original.WorkFolder;
        _theme.SelectedIndex = (int)_original.Theme;
        _countryLanguage.SelectedIndex = _original.CountryLanguage == "en" ? 1 : 0;
        _ctrlMouseWheelZoom.Checked = _original.CtrlMouseWheelZoom;
        _pageSize.Value = _original.PageSize;
        _readEncoding.SelectedIndex = _original.DefaultEncodingName == "auto" ? 0 : Array.IndexOf(Encodings, _original.DefaultEncodingName) + 1;
        _saveEncoding.SelectedIndex = Math.Max(0, Array.IndexOf(Encodings, _original.DefaultSaveEncodingName));
        _delimiter.SelectedIndex = _original.DefaultDelimiter switch { '\t' => 1, ';' => 2, _ => 0 };
        _hasHeader.Checked = _original.DefaultHasHeader;
        _confirmRead.Checked = _original.ConfirmReadSettings;
        _writeBom.Checked = _original.DefaultWriteBom;
        _gridFamily = _original.GridFontFamily;
        _editorFamily = _original.EditorFontFamily;
        _gridSize.Value = (decimal)_original.GridFontSize;
        _editorSize.Value = (decimal)_original.EditorFontSize;
        _zoom.Value = _original.ZoomPercent;
        _sqlLayout.SelectedIndex = (int)_original.SqlLayout;
        _rememberWindow.Checked = _original.RememberWindowBounds;
        _startMaximized.Checked = _original.StartMaximized;
        _inspectorVisible.Checked = _original.InspectorVisible;
        RefreshFontSamples();
        RefreshShortcuts();
    }

    private void ApplyValues()
    {
        var result = _original.Clone();
        result.WorkFolder = _workFolder.Text.Trim();
        result.Theme = (AppTheme)_theme.SelectedIndex;
        result.CountryLanguage = _countryLanguage.SelectedIndex == 1 ? "en" : "ja";
        result.CtrlMouseWheelZoom = _ctrlMouseWheelZoom.Checked;
        result.PageSize = (int)_pageSize.Value;
        result.DefaultEncodingName = _readEncoding.SelectedIndex <= 0 ? "auto" : Encodings[_readEncoding.SelectedIndex - 1];
        result.DefaultSaveEncodingName = Encodings[Math.Max(0, _saveEncoding.SelectedIndex)];
        result.DefaultDelimiter = _delimiter.SelectedIndex switch { 1 => '\t', 2 => ';', _ => ',' };
        result.DefaultHasHeader = _hasHeader.Checked;
        result.ConfirmReadSettings = _confirmRead.Checked;
        result.DefaultWriteBom = _writeBom.Checked;
        result.GridFontFamily = _gridFamily;
        result.GridFontSize = (float)_gridSize.Value;
        result.EditorFontFamily = _editorFamily;
        result.EditorFontSize = (float)_editorSize.Value;
        result.ZoomPercent = (int)_zoom.Value;
        result.SqlLayout = (SqlPaneLayout)_sqlLayout.SelectedIndex;
        result.RememberWindowBounds = _rememberWindow.Checked;
        result.StartMaximized = _startMaximized.Checked;
        result.InspectorVisible = _inspectorVisible.Checked;
        result.Shortcuts = new(_shortcutValues, StringComparer.Ordinal);
        try { result.Validate(); }
        catch (ArgumentException ex) { MessageBox.Show(this, ex.Message, "設定を確認してください", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        Result = result;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void AddPage(string name, Control page)
    {
        _navigation.Items.Add(name);
        page.Visible = false;
        page.Dock = DockStyle.Fill;
        _pages.Add(page);
        _content.Controls.Add(page);
    }

    private void SelectPage()
    {
        for (var i = 0; i < _pages.Count; i++) _pages[i].Visible = i == _navigation.SelectedIndex;
        _navigation.Invalidate();
    }

    private void DrawNavigation(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var selected = (e.State & DrawItemState.Selected) != 0;
        using var background = new SolidBrush(selected ? UiTheme.Selection : UiTheme.Header);
        e.Graphics.FillRectangle(background, e.Bounds);
        if (selected)
        {
            using var accent = new SolidBrush(UiTheme.Accent);
            e.Graphics.FillRectangle(accent, e.Bounds.X, e.Bounds.Y + 7, 3, e.Bounds.Height - 14);
        }
        var bounds = new Rectangle(e.Bounds.X + 11, e.Bounds.Y, e.Bounds.Width - 15, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, _navigation.Items[e.Index].ToString(), Font, bounds, selected ? UiTheme.Accent : UiTheme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }

    private static FlowLayoutPanel Page(string title, string description)
    {
        var page = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, AutoScrollMargin = new Size(0, 16), Padding = Padding.Empty };
        page.Controls.Add(Heading(title));
        page.Controls.Add(Hint(description));
        void Fit()
        {
            var width = Math.Max(250, page.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4);
            foreach (Control control in page.Controls)
            {
                control.Width = width;
                if (control is Label { AutoSize: true } label) label.MaximumSize = new Size(width, 0);
                if (control is CheckBox checkbox) checkbox.MaximumSize = new Size(width, 0);
            }
        }
        page.SizeChanged += (_, _) => Fit();
        return page;
    }

    private static Label Heading(string title) => new() { Text = title, AutoSize = true, Font = new Font("Yu Gothic UI", 12, FontStyle.Bold), Margin = new Padding(0, 0, 0, 10) };
    private static Label Hint(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = UiTheme.Muted, Margin = new Padding(0, 0, 0, 17) };
    private static CheckBox Check(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(0, 0, 0, 13) };
    private static ComboBox Choices(params string[] choices)
    {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320, SelectedIndex = -1 };
        combo.Items.AddRange(choices);
        return combo;
    }
    private static NumericUpDown Number(decimal minimum, decimal maximum, decimal value, int decimals, decimal increment) => new()
    { Minimum = minimum, Maximum = maximum, Value = value, DecimalPlaces = decimals, Increment = increment, Width = 130, ThousandsSeparator = true };
    private static Control Field(string label, Control input)
    {
        var field = new TableLayoutPanel { Height = input is TableLayoutPanel ? 78 : 64, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 0, 0, 13), Padding = Padding.Empty };
        field.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        field.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        field.Controls.Add(new Label { Text = label, AutoSize = true, Margin = Padding.Empty }, 0, 0);
        input.Margin = Padding.Empty;
        input.Anchor = input is NumericUpDown ? AnchorStyles.Left | AnchorStyles.Top : AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        field.Controls.Add(input, 0, 1);
        return field;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_gridSample.Tag is Font grid) { _gridSample.Tag = null; grid.Dispose(); }
            if (_editorSample.Tag is Font editor) { _editorSample.Tag = null; editor.Dispose(); }
        }
        base.Dispose(disposing);
    }
}
