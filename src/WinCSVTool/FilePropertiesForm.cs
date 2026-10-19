using WinCSVTool.Core;

namespace WinCSVTool;

/// <summary>Metadata only: inspecting a large CSV never scans its rows.</summary>
internal sealed class FilePropertiesForm : Form
{
    private readonly Font _appearanceFont;
    public FilePropertiesForm(CsvDocument document, Workspace workspace, AppSettings settings)
    {
        Text = "ファイルのプロパティ — " + document.DisplayName;
        Width = 790;
        Height = 690;
        MinimumSize = new Size(620, 480);
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ShowInTaskbar = false;
        var tabs = new ModernTabControl { Dock = DockStyle.Fill };
        var general = new TabPage("ファイル情報") { Padding = new Padding(16), Tag = false };
        var columns = new TabPage("列の情報") { Padding = new Padding(16), Tag = false };
        tabs.TabPages.AddRange([general, columns]);
        var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 0, 0, 12) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 166));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        scroll.Controls.Add(table);
        general.Controls.Add(scroll);
        void Property(string name, string value)
        {
            var row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var label = new Label { Text = name, AutoSize = true, Padding = new Padding(0, 7, 8, 6), Dock = DockStyle.Fill };
            var content = new TextBox { Text = value, ReadOnly = true, BorderStyle = BorderStyle.None, Multiline = true, WordWrap = true, TabStop = true, Dock = DockStyle.Fill, Margin = new Padding(0, 7, 0, 6) };
            void FitHeight() => content.Height = Math.Max(content.Font.Height + 4, TextRenderer.MeasureText(value, content.Font, new Size(Math.Max(80, content.Width), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height + 4);
            content.SizeChanged += (_, _) => FitHeight();
            table.Controls.Add(label, 0, row);
            table.Controls.Add(content, 1, row);
            FitHeight();
        }
        var sourcePath = document.SourcePath ?? document.ImportSourcePath;
        var source = GetFileInfo(sourcePath);
        Property("名前", document.DisplayName);
        Property("元ファイルの場所", sourcePath ?? "未保存の新規CSV");
        if (document.ImportSourcePath is { } logSource) Property("取り込み元のログ", logSource + "（CSV保存で上書きしません）");
        Property("元ファイルの状態", source is null ? sourcePath is null ? "新規CSV（保存先未設定）" : "見つからない、または参照できません" : "存在します");
        Property("元ファイルのサイズ", source is null ? "—" : FileSize(source.Length));
        Property("元ファイルの更新日時", source is null ? "—" : source.LastWriteTime.ToString("yyyy/MM/dd HH:mm:ss"));
        Property("文字コード", document.EncodingName);
        Property("区切り文字", document.Delimiter switch { '\t' => "タブ (TAB)", ',' => "カンマ (,)", ';' => "セミコロン (;)", '|' => "パイプ (|)", _ => document.Delimiter.ToString() });
        Property("先頭行をヘッダーとして使用", document.HasHeader ? "はい" : "いいえ");
        Property("現在のデータ行数", $"{document.RowCount:N0} 行（ヘッダーを除く）");
        Property("現在の列数", $"{document.Columns.Count:N0} 列");
        Property("編集状態", document.IsDirty ? "未保存の変更があります" : "保存済み");
        Property("SQLのテーブル名", document.TableName);
        Property("作業キャッシュの場所", workspace.RootPath);
        long cacheSize = 0;
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var file = GetFileInfo(workspace.DatabasePath + suffix);
            if (file is not null) cacheSize += file.Length;
        }
        Property("作業キャッシュのサイズ", FileSize(cacheSize) + "（開いている全CSVの合計）");
        var note = new Label { Text = "行数・列数は編集中のデータ全体です。ソート・フィルターや表示上限によって変わりません。元ファイルのサイズと更新日時はディスク上の情報です。", Dock = DockStyle.Bottom, Height = 58, Padding = new Padding(0, 8, 0, 0) };
        general.Controls.Add(note);
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, VirtualMode = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false, RowHeadersVisible = false, BorderStyle = BorderStyle.None,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, EnableHeadersVisualStyles = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false
        };
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "index", HeaderText = "列", FillWeight = 35, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "header", HeaderText = "表示名", FillWeight = 100, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "sql", HeaderText = "SQLの列名", FillWeight = 100, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "type", HeaderText = "データ型", FillWeight = 65, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.CellValueNeeded += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= document.Columns.Count) return;
            var column = document.Columns[e.RowIndex];
            e.Value = e.ColumnIndex switch
            {
                0 => (e.RowIndex + 1).ToString("N0"), 1 => column.HeaderName, 2 => column.Name,
                3 => column.Kind switch { DataKind.Auto => "自動判定", DataKind.Text => "テキスト", DataKind.Number => "数値", DataKind.Timestamp => "日時", DataKind.Boolean => "真偽値", _ => column.Kind.ToString() }, _ => ""
            };
        };
        columns.Controls.Add(grid);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 60, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12, 10, 12, 10) };
        var close = new Button { Text = "閉じる", DialogResult = DialogResult.OK, AutoSize = true, Height = 34 };
        var copy = new Button { Text = "ファイルの場所をコピー", AutoSize = true, Height = 34, Enabled = document.SourcePath is not null };
        copy.Click += (_, _) =>
        {
            try { if (document.SourcePath is { } path) Clipboard.SetText(path); }
            catch (System.Runtime.InteropServices.ExternalException) { MessageBox.Show(this, "クリップボードを使用できません。少し待ってからもう一度お試しください。", "場所のコピー", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        };
        actions.Controls.Add(close);
        actions.Controls.Add(copy);
        Controls.Add(tabs);
        Controls.Add(actions);
        AcceptButton = CancelButton = close;
        UiTheme.ThemeForm(this);
        UiTheme.StyleButton(close, true);
        UiTheme.StyleButton(copy);
        note.ForeColor = UiTheme.Muted;
        foreach (var box in table.Controls.OfType<TextBox>()) box.BorderStyle = BorderStyle.None;
        grid.BackgroundColor = UiTheme.Surface;
        grid.GridColor = UiTheme.Border;
        grid.DefaultCellStyle.BackColor = UiTheme.Surface;
        grid.DefaultCellStyle.ForeColor = UiTheme.Text;
        grid.DefaultCellStyle.SelectionBackColor = UiTheme.Selection;
        grid.DefaultCellStyle.SelectionForeColor = UiTheme.Text;
        grid.DefaultCellStyle.Font = _appearanceFont = UiTheme.CreateGridFont(settings);
        grid.ColumnHeadersDefaultCellStyle.BackColor = UiTheme.Header;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = UiTheme.Text;
        grid.ColumnHeadersDefaultCellStyle.Font = Font;
        grid.RowTemplate.Height = Math.Max(28, grid.DefaultCellStyle.Font.Height + 10);
        grid.RowCount = document.Columns.Count;
        grid.ColumnHeadersHeight = 34;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _appearanceFont.Dispose();
    }

    private static FileInfo? GetFileInfo(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try { var info = new FileInfo(path); return info.Exists ? info : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    private static string FileSize(long bytes) => bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):N2} GiB ({bytes:N0} バイト)" : bytes >= 1024L * 1024 ? $"{bytes / (1024d * 1024):N2} MiB ({bytes:N0} バイト)" : bytes >= 1024 ? $"{bytes / 1024d:N2} KiB ({bytes:N0} バイト)" : $"{bytes:N0} バイト";
}
