using WinCSVTool.Core;

namespace WinCSVTool;

internal sealed class LogAttackPage : LogFeaturePage
{
    private readonly CheckedListBox _columns = new() { CheckOnClick = true, IntegralHeight = false, Height = 145, Dock = DockStyle.Fill };
    private readonly DataGridView _rules = ResultGrid("分類", "主な確認対象");
    private readonly DataGridView _preview = ResultGrid("データ行", "分類", "理由", "確認状態");
    private CsvDocument? _boundDocument;
    private string[] _columnNames = [];
    internal LogAttackPage(MainForm owner) : base(owner, "攻撃パターン分類", "選択したリクエスト・URI・クエリ・UAなどをルールで照合し、分類・理由・確認状態の列を追加します。複数分類に対応します。実行まで全件の判定は行いません。")
    {
        AddField("照合する列（複数選択）", _columns);
        AddAction("リクエスト系を選択", () => { for (var i = 0; i < _columns.Items.Count; i++) _columns.SetItemChecked(i, IsRequestColumn(_columns.Items[i].ToString() ?? "")); return Task.CompletedTask; });
        AddAction("全列を選択", () => { for (var i = 0; i < _columns.Items.Count; i++) _columns.SetItemChecked(i, true); return Task.CompletedTask; });
        AddAction("選択を解除", () => { for (var i = 0; i < _columns.Items.Count; i++) _columns.SetItemChecked(i, false); return Task.CompletedTask; });
        AddAction("先頭100行で確認", PreviewAsync); AddAction("実行：分類列を追加", ClassifyAsync);
        Status.Text = "表全体を対象に、URLデコードを最大2回行って照合します。元の値は変更しません。機械的な候補判定のため、誤検出や未検出があります。";
        foreach (var (name, description) in new[] { ("SQLi", "SQL構文・注入の特徴"), ("XSS", "スクリプト・イベント属性など"), ("Path Traversal", "親ディレクトリへの参照"), ("LFI", "ローカルファイル・既知の設定ファイルへの参照"), ("RCE", "コマンドやコード実行の特徴"), ("SSRF", "内部サービス・メタデータURLへの参照"), ("スキャン", "既知の探索パス・ツールの特徴") }) _rules.Rows.Add(name, description);
        AddContent(_preview);
        var showRules = new CheckBox { Text = "ルールの概要を表示", AutoSize = true };
        showRules.CheckedChanged += (_, _) => _rules.Visible = showRules.Checked; AddContent(showRules); AddContent(_rules); _rules.Visible = false;
    }
    protected override void RefreshColumns()
    {
        var sameDocument = ReferenceEquals(_boundDocument, Document);
        var old = sameDocument ? _columns.CheckedIndices.Cast<int>().Where(index => index < _columnNames.Length).Select(index => _columnNames[index]).ToHashSet(StringComparer.Ordinal) : [];
        _columns.Items.Clear();
        if (Document is { } document)
            foreach (var column in document.Columns)
            {
                var guess = !sameDocument && IsRequestColumn(column.HeaderName);
                _columns.Items.Add(column.HeaderName, old.Contains(column.Name) || guess);
            }
        _boundDocument = Document; _columnNames = Document?.Columns.Select(column => column.Name).ToArray() ?? [];
    }
    private static bool IsRequestColumn(string name) => new[] { "%r", "%U", "%q", "r", "U", "q", "cs-uri-stem", "cs-uri-query" }.Contains(name, StringComparer.Ordinal) ||
        new[] { "request", "uri", "url", "args", "query", "agent", "リクエスト", "UA" }.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));
    protected override void DocumentChanged() { _preview.Rows.Clear(); Status.Text = "照合するリクエスト・URI・UAなどを選び、先頭100行で確認できます。確認操作では列を追加しません。"; }
    private async Task PreviewAsync()
    {
        var document = RequireDocument(); var columns = _columns.CheckedIndices.Cast<int>().ToArray();
        if (columns.Length == 0) throw new InvalidOperationException("照合する列を1つ以上選択してください。");
        var items = new List<(long Row, AttackCategory Categories, string Reasons, bool Incomplete)>();
        if (!await Owner.RunWork("先頭100行の攻撃候補を確認", token =>
        {
            var ordinal = 0L;
            foreach (var row in Owner.Workspace.ReadPage(document, new ViewSpec(), 0, 100, token))
            {
                token.ThrowIfCancellationRequested(); var categories = AttackCategory.None; var reasons = new HashSet<string>(); var incomplete = false;
                foreach (var column in columns)
                {
                    var result = AttackPatternClassifier.Classify(row.Values[column]); categories |= result.Categories; incomplete |= result.Incomplete;
                    if (result.Reasons.Length > 0) reasons.Add(document.Columns[column].HeaderName + ": " + result.Reasons);
                }
                items.Add((++ordinal, categories, string.Join("; ", reasons), incomplete));
            }
        })) return;
        _preview.Rows.Clear(); foreach (var item in items) _preview.Rows.Add(item.Row, AttackPatternClassifier.FormatCategories(item.Categories), item.Reasons, item.Incomplete ? "上限あり・要確認" : "ルールによる参考判定");
        Status.Text = $"先頭{items.Count:N0}行を確認、候補{items.Count(item => item.Categories != AttackCategory.None):N0}行。対象列: {string.Join("、", columns.Select(index => document.Columns[index].HeaderName))}。表は変更していません。";
    }
    private async Task ClassifyAsync()
    {
        var document = RequireDocument(); var columns = _columns.CheckedIndices.Cast<int>().ToArray();
        if (columns.Length == 0) throw new InvalidOperationException("照合する列を1つ以上選択してください。");
        if (!Owner.ConfirmUnsupportedUndo($"全{document.RowCount:N0}行への攻撃パターン分類列の追加")) return;
        LogTransformSummary? summary = null;
        await Owner.RunWork("攻撃パターンを分類", token => summary = Owner.Workspace.AddAttackClassificationColumns(document, columns, token: token));
        if (summary is not null)
        {
            await Owner.RefreshLogColumnsAsync(document);
            Status.Text = $"完了：{summary.ProcessedRows:N0}行、候補 {summary.MatchedRows:N0}行、判定を完了できない行 {summary.InvalidRows:N0}行。分類と理由を確認してから保存してください。";
        }
    }
}

internal sealed class LogStatisticsPage : LogFeaturePage
{
    private readonly ComboBox _column;
    private readonly ComboBox _scope = Choice();
    private readonly DataGridView _grid = ResultGrid("列", "統計", "値");
    internal LogStatisticsPage(MainForm owner) : base(owner, "基本統計", "対象列と範囲を選び、［実行］で合計・平均・最頻値・分散・標準偏差などを取得します。文字列・空欄・日時の統計にも対応します。")
    {
        _column = AddColumn("対象列", true); _scope.Items.AddRange(["表全体", "現在のフィルター結果", "表で選択した行と列"]); _scope.SelectedIndex = 0;
        AddField("対象範囲", _scope); AddAction("実行", AnalyzeAsync); AddContent(_grid);
        _scope.SelectedIndexChanged += (_, _) => _column.Enabled = _scope.SelectedIndex != 2;
        Status.Text = "統計はまだ取得していません。列や範囲の変更だけでは処理を開始しません。";
    }
    protected override void RefreshColumns()
    {
        base.RefreshColumns(); if (_column.Items.Count > 0) _column.Items[0] = "すべての列";
    }
    protected override void DocumentChanged() { base.DocumentChanged(); _grid.Rows.Clear(); }
    private async Task AnalyzeAsync()
    {
        var document = RequireDocument(); var scope = _scope.SelectedIndex;
        var page = Owner.DocumentPages.FirstOrDefault(page => page.Document == document);
        var view = scope == 1 ? page?.View ?? new ViewSpec() : new ViewSpec();
        var ids = scope == 2 ? page?.SelectedRowIds() ?? [] : null;
        var columns = scope == 2 ? page?.Selection().Columns ?? [] : OptionalColumn(_column) is { } column ? [column] : Enumerable.Range(0, document.Columns.Count).ToArray();
        if (columns.Length == 0) throw new InvalidOperationException("表で統計を取得するセルを選択してください。");
        var results = new List<(int Column, StatisticsResult Result)>();
        if (!await Owner.RunWork("統計を計算", token =>
        {
            var service = new AnalysisService(Owner.Workspace);
            foreach (var index in columns) results.Add((index, scope == 2 ? service.StatsForRows(document, ids!, index, token) : service.Stats(document, view, index, token: token)));
        })) return;
        _grid.Rows.Clear(); foreach (var result in results) foreach (var item in result.Result.Items) _grid.Rows.Add(document.Columns[result.Column].HeaderName, item.Key, item.Value);
        Status.Text = $"{document.DisplayName} / {columns.Length:N0}列 / {_scope.Items[scope]} の実行結果です。";
    }
}
