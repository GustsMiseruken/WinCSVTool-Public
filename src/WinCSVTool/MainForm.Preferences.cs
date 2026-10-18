using WinCSVTool.Core;

namespace WinCSVTool;

internal sealed partial class MainForm
{
    internal AppSettings Settings { get; private set; } = new();
    private readonly SettingsStore _settingsStore;
    private readonly Dictionary<string, ToolStripMenuItem> _shortcutItems = [];
    private readonly SplitContainer _contentSplit = new() { Dock = DockStyle.Fill, Panel2Collapsed = true, SplitterWidth = 6, Size = new Size(1200, 600) };
    private readonly Button _applyCellButton = new() { Text = "セルに反映", Dock = DockStyle.Right, Width = 128 };
    private readonly TrackBar _zoomSlider = new() { Minimum = 50, Maximum = 200, Value = 100, TickStyle = TickStyle.None, SmallChange = 5, LargeChange = 10, AutoSize = false, Size = new Size(112, 23), AccessibleName = "表示倍率" };
    private readonly ToolStripDropDownButton _zoomText = new("100%") { ToolTipText = "表示倍率を選択（表・セル内容・SQL）" };
    private readonly System.Windows.Forms.Timer _zoomTimer = new() { Interval = 180 };
    private ToolStripMenuItem? _inspectorMenu;
    private TabPage? _sqlTab;
    private bool _updatingZoom;
    private bool _movingSql;
    private bool _updatingAppearance;
    private Font? _inspectorFont;
    private ZoomWheelFilter? _wheelFilter;
    internal bool OwnsSqlForm(Form? form) => form is not null && ReferenceEquals(form, _sqlForm);

    private void InitializePreferences()
    {
        _status.Items.Add(new ToolStripSeparator());
        var minus = new ToolStripButton("−") { ToolTipText = "表示を縮小", AccessibleName = "表示を縮小", AutoSize = false, Width = 26 };
        var plus = new ToolStripButton("＋") { ToolTipText = "表示を拡大", AccessibleName = "表示を拡大", AutoSize = false, Width = 26 };
        minus.Click += (_, _) => SetZoomPercent(Settings.ZoomPercent - 10);
        plus.Click += (_, _) => SetZoomPercent(Settings.ZoomPercent + 10);
        foreach (var percent in new[] { 50, 75, 100, 125, 150, 175, 200 })
        {
            var item = new ToolStripMenuItem(percent + "%") { Tag = percent };
            item.Click += (_, _) => SetZoomPercent(percent); _zoomText.DropDownItems.Add(item);
        }
        _status.Items.Add(minus);
        _status.Items.Add(new ToolStripControlHost(_zoomSlider) { AutoSize = false, Size = new Size(112, 23), Margin = new Padding(0) });
        _status.Items.Add(plus); _status.Items.Add(_zoomText);
        _zoomSlider.ValueChanged += (_, _) =>
        {
            if (_updatingZoom) return;
            _zoomText.Text = _zoomSlider.Value + "%"; _zoomTimer.Stop(); _zoomTimer.Start();
        };
        _zoomSlider.MouseUp += (_, _) => { if (!_busy) SetZoomPercent(_zoomSlider.Value); };
        _zoomTimer.Tick += (_, _) => { _zoomTimer.Stop(); if (_busy) { SynchronizeZoom(); return; } SetZoomPercent(_zoomSlider.Value); };
        _contentSplit.SplitterMoved += (_, _) =>
        {
            if (_movingSql || _contentSplit.Panel2Collapsed) return;
            var total = _contentSplit.Orientation == Orientation.Vertical ? _contentSplit.ClientSize.Width : _contentSplit.ClientSize.Height;
            if (total > 0) Settings.SqlPaneRatio = Math.Clamp(1f - _contentSplit.SplitterDistance / (float)total, .2f, .8f);
        };
        _wheelFilter = new ZoomWheelFilter(this); Application.AddMessageFilter(_wheelFilter);
        Disposed += (_, _) => { if (_wheelFilter is not null) Application.RemoveMessageFilter(_wheelFilter); _zoomTimer.Dispose(); _inspectorFont?.Dispose(); _sqlForm?.Dispose(); _ipReference?.Dispose(); };
        RestoreWindowPreferences(); ApplyAppearance(); ApplyShortcuts();
    }

    private void ApplyAppearance()
    {
        _updatingAppearance = true;
        try
        {
            UiTheme.SetTheme(Settings.Theme); UiTheme.Apply(this);
            _menu.BackColor = _status.BackColor = _inspector.BackColor = UiTheme.Bg;
            _tools.BackColor = UiTheme.Surface;
            _state.ForeColor = _fileInfo.ForeColor = _cellLabel.ForeColor = UiTheme.Muted;
            _address.ForeColor = UiTheme.Accent; _address.BackColor = _cellLabel.BackColor = UiTheme.Bg;
            _zoomSlider.BackColor = UiTheme.Bg;
            _cellText.BorderStyle = BorderStyle.None;
            var candidateFont = UiTheme.CreateEditorFont(Settings);
            if (_inspectorFont is not null && _inspectorFont.Equals(candidateFont)) candidateFont.Dispose();
            else { var previous = _inspectorFont; _inspectorFont = candidateFont; _cellText.Font = _inspectorFont; previous?.Dispose(); }
            UiTheme.StyleButton(_applyCellButton, true);
            foreach (var page in DocumentPages) page.ApplyAppearance(Settings);
            if (_emptyGrid?.IsDisposed == false) _emptyGrid.ApplyAppearance(Settings);
            if (_sqlForm?.IsDisposed == false) _sqlForm.ApplyAppearance(Settings);
            _inspector.Visible = Settings.InspectorVisible && _tabs.SelectedTab != _sqlTab && _tabs.SelectedTab != _logTab;
            _logAnalysis?.ApplyAppearance();
            if (_inspectorMenu is not null) _inspectorMenu.Checked = Settings.InspectorVisible;
            SynchronizeZoom(); _tabs.Invalidate();
        }
        finally { _updatingAppearance = false; }
    }

    private void ApplyShortcuts()
    {
        foreach (var item in _shortcutItems.Values) item.ShortcutKeys = Keys.None;
        foreach (var (id, item) in _shortcutItems) item.ShortcutKeys = Settings.GetShortcut(id);
        foreach (var button in _tools.Items.OfType<ToolStripButton>())
        {
            var id = button.Text switch { "開く" => "open", "新規" => "new", "保存" => "save", "検索 / 置換" => "find", "SQL分析" => "sql", "デコード" => "decode", "ソート / フィルター" => "sortFilter", "統計" => "stats", "折り返し" => "wrap", _ => null };
            if (id is null) continue;
            var keys = Settings.GetShortcut(id);
            button.ToolTipText = button.Text + (keys == Keys.None ? "" : "  (" + ShortcutCatalog.Format(keys) + ")");
        }
    }

    private void SynchronizeZoom()
    {
        _updatingZoom = true;
        try
        {
            _zoomSlider.Value = Math.Clamp(Settings.ZoomPercent, 50, 200); _zoomText.Text = Settings.ZoomPercent + "%";
            foreach (ToolStripMenuItem item in _zoomText.DropDownItems) item.Checked = item.Tag is int value && value == Settings.ZoomPercent;
        }
        finally { _updatingZoom = false; }
    }

    internal void SetZoomPercent(int percent, bool persist = true)
    {
        if (_busy) return;
        _zoomTimer.Stop(); Settings.ZoomPercent = Math.Clamp(percent, 50, 200); ApplyAppearance();
        if (persist) TrySaveSettings();
    }

    private async Task ShowSettingsAsync()
    {
        if (_busy) return;
        using var dialog = new SettingsForm(Settings);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await ApplyPreferencesAsync(dialog.Result, true);
    }

    internal async Task ApplyPreferencesAsync(AppSettings next, bool persist)
    {
        if (_busy) return;
        next.Validate();
        foreach (var page in DocumentPages) page.Grid.EndEdit();
        if (persist)
        {
            try { _settingsStore.Save(next); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowError(ex); return; }
        }
        var rowsChanged = Settings.PageSize != next.PageSize;
        var layoutChanged = Settings.SqlLayout != next.SqlLayout;
        var workChanged = !string.Equals(Settings.WorkFolder, next.WorkFolder, StringComparison.OrdinalIgnoreCase);
        Settings = next.Clone(); ApplyAppearance(); ApplyShortcuts();
        if (layoutChanged && _sqlForm?.IsDisposed == false) ShowSql(Settings.SqlLayout);
        if (rowsChanged)
        {
            foreach (var page in DocumentPages.ToArray())
                await page.ReloadAsync(page.Offset / page.PageSize * page.PageSize);
        }
        _state.Text = workChanged ? "環境設定を適用しました — 作業データの保存先は次回起動時に変更されます" : "環境設定を適用しました";
    }

    private void TrySaveSettings()
    {
        try { _settingsStore.Save(Settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _state.Text = "設定を保存できませんでした: " + ex.Message; }
    }

    private void RestoreWindowPreferences()
    {
        if (Settings.RememberWindowBounds && Settings.WindowBounds is { } saved)
        {
            var bounds = new Rectangle(saved.X, saved.Y, Math.Max(MinimumSize.Width, saved.Width), Math.Max(MinimumSize.Height, saved.Height));
            if (Screen.AllScreens.Any(screen => Rectangle.Intersect(screen.WorkingArea, bounds) is { Width: >= 100, Height: >= 100 }))
            { StartPosition = FormStartPosition.Manual; Bounds = bounds; }
        }
        if (Settings.StartMaximized || Settings.RememberWindowBounds && Settings.WasMaximized) WindowState = FormWindowState.Maximized;
    }

    private void SaveWindowPreferences()
    {
        if (Settings.RememberWindowBounds)
        {
            var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            if (bounds.Width > 0 && bounds.Height > 0) Settings.WindowBounds = new WindowBounds { X = bounds.X, Y = bounds.Y, Width = bounds.Width, Height = bounds.Height };
            Settings.WasMaximized = WindowState == FormWindowState.Maximized;
        }
        TrySaveSettings();
    }

    internal CsvOptions DefaultReadOptions() => new(Settings.DefaultEncodingName, Settings.DefaultHasHeader, Settings.DefaultDelimiter);
    private CsvDocument DefaultReadDocument() => new() { TableName = "defaults", DisplayName = "", EncodingName = Settings.DefaultEncodingName, HasHeader = Settings.DefaultHasHeader, Delimiter = Settings.DefaultDelimiter };
    internal CsvDocument DefaultSaveDocument() => new() { TableName = "defaults", DisplayName = "", EncodingName = Settings.DefaultSaveEncodingName, HasHeader = Settings.DefaultHasHeader, Delimiter = Settings.DefaultDelimiter };

    private Task ShowPropertiesAsync()
    {
        if (Current is { } page) { using var dialog = new FilePropertiesForm(page.Document, Workspace, Settings); dialog.ShowDialog(this); }
        return Task.CompletedTask;
    }

    internal Task SetSqlLayoutAsync(SqlPaneLayout layout)
    {
        if (_busy || _sqlForm?.IsRunning == true) return Task.CompletedTask;
        Settings.SqlLayout = layout; ShowSql(layout); TrySaveSettings(); return Task.CompletedTask;
    }

    private void ShowSql(SqlPaneLayout layout)
    {
        if (_busy || _sqlForm?.IsRunning == true) return;
        if (_sqlForm is null || _sqlForm.IsDisposed)
        {
            _sqlForm = new SqlForm(this);
            _sqlForm.FormClosed += (_, _) => { if (!IsDisposed && !Disposing) { DetachSql(); ShowEmptySheet(); UpdateInspector(); } };
        }
        _movingSql = true;
        try
        {
            _sqlForm.Hide(); DetachSql();
            _sqlForm.PrepareHost(layout != SqlPaneLayout.SeparateWindow);
            switch (layout)
            {
                case SqlPaneLayout.SeparateWindow:
                    _sqlForm.Show(this); _sqlForm.BringToFront(); break;
                case SqlPaneLayout.Tab:
                    _sqlTab = new TabPage("SQL分析") { BackColor = UiTheme.Surface, Tag = "sql" };
                    _sqlTab.Controls.Add(_sqlForm); _tabs.TabPages.Add(_sqlTab); _tabs.SelectedTab = _sqlTab; _sqlForm.Show(); break;
                default:
                    _contentSplit.Panel1MinSize = _contentSplit.Panel2MinSize = 0;
                    _contentSplit.Orientation = layout == SqlPaneLayout.Right ? Orientation.Vertical : Orientation.Horizontal;
                    _contentSplit.Panel2Collapsed = false; _contentSplit.Panel2.Controls.Add(_sqlForm);
                    var total = layout == SqlPaneLayout.Right ? _contentSplit.ClientSize.Width : _contentSplit.ClientSize.Height;
                    var available = Math.Max(2, total - _contentSplit.SplitterWidth);
                    _contentSplit.SplitterDistance = Math.Clamp((int)(available * (1 - Settings.SqlPaneRatio)), 1, available - 1);
                    _sqlForm.Show(); break;
            }
            _sqlForm.ApplyAppearance(Settings); _sqlForm.RefreshSchema(); UpdateInspector();
        }
        finally { _movingSql = false; }
    }

    private void DetachSql()
    {
        if (_sqlForm?.Parent is { } parent) parent.Controls.Remove(_sqlForm);
        if (_sqlTab is not null) { _tabs.TabPages.Remove(_sqlTab); _sqlTab.Dispose(); _sqlTab = null; }
        _contentSplit.Panel2Collapsed = true;
    }

    private void HideSql()
    {
        if (_sqlForm?.IsRunning == true) { CancelWork(); return; }
        _sqlForm?.Hide(); DetachSql(); ShowEmptySheet(); UpdateInspector();
    }
}
