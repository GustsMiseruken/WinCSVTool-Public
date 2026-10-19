using System.Globalization;
using System.Text;
using WinCSVTool.Core;

namespace WinCSVTool;

/// <summary>A stable settings pane and cached results, with explicit security-investigation entry points.</summary>
internal sealed class LogTimeSeriesPage : LogFeaturePage
{
    private sealed record ShapeChoice(int Id, string Title) { public override string ToString() => Title; }
    private readonly MetricAxisCard _axisA;
    private MetricAxisCard? _axisB;
    private readonly ComboBox _preset = Choice(), _bucket = Choice(), _zone = Choice(), _shape = Choice();
    private readonly FeatureTabs _axisTabs = new() { Dock = DockStyle.Fill }, _resultTabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage _axisATab = new("調査 A"), _axisBTab = new("比較 B"), _commonTab = new("集計・比較"), _graphTab = new("グラフ"), _dataTab = new("集計表");
    private readonly SplitContainer _body = new() { Dock = DockStyle.Fill, Size = new Size(1000, 650), SplitterDistance = 345, Panel1MinSize = 250, Panel2MinSize = 180, SplitterWidth = 5 };
    private readonly CheckBox _showBands = new() { Text = "平均 ± Nσ", AutoSize = true }, _showOutliers = new() { Text = "外れ値をマーク", Checked = true, AutoSize = true };
    private readonly Button _run, _addAxis, _removeAxis, _previous, _next, _export, _toggleSettings, _details;
    private readonly Label _position = new() { AutoSize = true, Margin = new Padding(5, 10, 0, 0) };
    private readonly Label _caption = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Height = 46, Padding = new Padding(5, 5, 5, 0) };
    private readonly FlowLayoutPanel _legend = new() { Dock = DockStyle.Fill, Height = 45, AutoScroll = true, WrapContents = true, Padding = new Padding(4), Visible = false };
    private readonly FlowLayoutPanel _pager = new() { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
    private readonly Label _comparisonNote = new() { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 4, 0, 0) };
    private readonly RowStyle _captionRow = new(SizeType.Absolute, 38);
    private readonly RowStyle _statusRow = new(SizeType.Absolute, 43);
    private readonly LogSeriesChart _chart = new() { Dock = DockStyle.Fill, MinimumSize = new Size(120, 130) };
    private readonly DataGridView _grid = ResultGrid("時刻", "系列", "値", "対象数");
    private readonly ToolTip _tips = new() { AutoPopDelay = 20000 };
    private MetricAnalysisResult? _result;
    private IReadOnlyList<MetricPoint> _points = [];
    private bool _dirty, _applyingPreset, _syncingShapes, _distributionShapes, _fittingPanes;
    private int _timeShape, _distributionShape = 4;
    private long _offset;

    internal LogTimeSeriesPage(MainForm owner) : base(owner, "グラフで調査", "")
    {
        // The old auto-sized scrolling stack is replaced once; settings never rebuild the result surface.
        Content.Controls.Remove(Actions); Content.Controls.Remove(Status);
        var oldScroll = Controls[0]; oldScroll.Controls.Remove(Content); Controls.Remove(oldScroll); oldScroll.Dispose(); Content.Dispose();
        DoubleBuffered = true;
        var shell = new FeatureTable { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10) };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize)); shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); shell.RowStyles.Add(_statusRow);
        Controls.Add(shell);
        _preset.Dock = DockStyle.None; _preset.Width = 260; _preset.Margin = new Padding(0, 5, 12, 5);
        _preset.Items.AddRange(["404エラーの時間推移", "国・地域の内訳", "指定した国からのアクセス", "指定した国を除くアクセス", "自由に設定"]);
        _preset.AccessibleName = "調査テンプレート"; Actions.Controls.Add(_preset);
        _run = AddAction("グラフを作成", AnalyzeAsync);
        _toggleSettings = AddAction("条件を隠す", () => { ToggleSettings(); return Task.CompletedTask; });
        _export = AddAction("CSVへ保存…", ExportAsync);
        _details = AddAction("結果の詳細…", ShowResultDetailsAsync);
        shell.Controls.Add(Actions, 0, 0); shell.Controls.Add(_body, 0, 1);
        Status.Margin = new Padding(3, 7, 3, 0); Status.AutoSize = false; Status.Dock = DockStyle.Fill; Status.Height = Font.Height + 12; Status.AutoEllipsis = true; shell.Controls.Add(Status, 0, 2);

        var settings = new FeatureTable { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1, Padding = new Padding(4, 8, 9, 4) };
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        settings.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _body.Panel1.Controls.Add(settings); settings.Controls.Add(_axisTabs, 0, 0);
        _axisA = new MetricAxisCard(owner, "調査 A"); _axisA.SettingsChanged += OnSettingsChanged; _axisATab.Controls.Add(_axisA); _axisTabs.TabPages.Add(_axisATab);
        var sharedScroll = new FeaturePanel { Dock = DockStyle.Fill, AutoScroll = true };
        var shared = new FeatureTable { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(8) };
        shared.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); sharedScroll.Controls.Add(shared); _commonTab.Controls.Add(sharedScroll); _axisTabs.TabPages.Add(_commonTab);
        var common = new FeatureTable { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(4) };
        common.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); common.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        common.Controls.Add(new Label { Text = "集計の間隔", AutoSize = true }, 0, 0); common.Controls.Add(new Label { Text = "表示する時刻", AutoSize = true }, 1, 0);
        _bucket.Items.AddRange(["1秒", "1分", "1時間"]); _bucket.SelectedIndex = 1; common.Controls.Add(_bucket, 0, 1);
        _zone.Items.AddRange(["UTC", "JST"]); _zone.SelectedIndex = 0; common.Controls.Add(_zone, 1, 1);
        _bucket.MinimumSize = _zone.MinimumSize = new Size(90, 0); shared.Controls.Add(common, 0, 0);
        var comparison = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        _addAxis = AddAction("＋ 比較を追加", AddAxisAsync); _removeAxis = AddAction("比較を外す", RemoveAxisAsync);
        comparison.Controls.Add(_addAxis); comparison.Controls.Add(_removeAxis); shared.Controls.Add(comparison, 0, 1); shared.Controls.Add(_comparisonNote, 0, 2);
        shared.Controls.Add(new Label { Text = "集計間隔と表示する時刻は、調査A・比較Bで共通です。変更は［グラフを作成］で反映します。", AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 12, 0, 0) }, 0, 3);
        var annotations = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(0, 12, 0, 0) };
        annotations.Controls.AddRange([_showBands, _showOutliers]); shared.Controls.Add(annotations, 0, 4);

        var results = new FeatureTable { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(9, 7, 2, 0) };
        results.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        results.RowStyles.Add(new RowStyle(SizeType.AutoSize)); results.RowStyles.Add(_captionRow);
        results.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); results.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _body.Panel2.Controls.Add(results);
        var display = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        display.Controls.Add(new Label { Text = "表示", AutoSize = true, Margin = new Padding(0, 8, 8, 0) });
        _shape.Dock = DockStyle.None; _shape.Width = 155; _shape.MinimumSize = new Size(100, 0); _shape.Margin = new Padding(0, 3, 12, 3);
        display.Controls.AddRange([_shape, _details]); results.Controls.Add(display, 0, 0); results.Controls.Add(_caption, 0, 1); results.Controls.Add(_resultTabs, 0, 2); results.Controls.Add(_pager, 0, 3);
        var graph = new FeatureTable { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        graph.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); graph.RowStyles.Add(new RowStyle(SizeType.AutoSize)); graph.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        graph.Controls.Add(_legend, 0, 0); graph.Controls.Add(_chart, 0, 1); _graphTab.Controls.Add(graph);
        var dataFooter = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        dataFooter.Controls.Add(_export); _grid.Dock = DockStyle.Fill; _grid.MinimumSize = Size.Empty; _dataTab.Controls.Add(_grid); _dataTab.Controls.Add(dataFooter); _resultTabs.TabPages.AddRange([_graphTab, _dataTab]);
        _previous = AddAction("前の1000点", () => LoadPageAsync(Math.Max(0, _offset - 1000))); _next = AddAction("次の1000点", () => LoadPageAsync(_offset + 1000));
        _pager.Controls.AddRange([_previous, _next, _position]);
        _body.SizeChanged += (_, _) => FitPanes(); SizeChanged += (_, _) => Status.MaximumSize = new Size(Math.Max(150, Width - 28), 0);
        _preset.SelectedIndexChanged += (_, _) => ApplySelectedPreset();
        _bucket.SelectedIndexChanged += (_, _) => OnSettingsChanged(); _zone.SelectedIndexChanged += (_, _) => OnSettingsChanged();
        _shape.SelectedIndexChanged += (_, _) =>
        {
            if (_syncingShapes) return;
            if (_distributionShapes) _distributionShape = SelectedShape; else _timeShape = SelectedShape;
            DrawChart();
        };
        _showBands.CheckedChanged += (_, _) => DrawChart(); _showOutliers.CheckedChanged += (_, _) => DrawChart();
        _tips.SetToolTip(_run, "選択した条件で全データを集計します。条件を変えただけでは実行しません。");
        _tips.SetToolTip(_preset, "調査テンプレートを選び、対象の表・列と条件を確認します。");
        _tips.SetToolTip(_shape, "取得済みの結果を描き直します。元のログは再集計しません。");
        _tips.SetToolTip(_addAxis, "時系列を別の条件・表と比較します。調査Aは左、比較Bは右の目盛りです。");
        _preset.SelectedIndex = 0; SyncShapeChoices(); UpdateActions();
        Status.Text = "対象の表と必要な列を確認し、［グラフを作成］を押してください。まだ集計していません。";
    }
    private IEnumerable<MetricAxisCard> ConfiguredAxes => _axisB is null ? [_axisA] : [_axisA, _axisB];
    private IEnumerable<MetricAxisCard> ActiveAxes => _axisA.IsDistribution ? [_axisA] : ConfiguredAxes;
    private int SelectedShape => (_shape.SelectedItem as ShapeChoice)?.Id ?? (_distributionShapes ? 4 : 0);
    private bool ResultIsDistribution => _result?.Axes[0].Mode == MetricMode.Distribution;
    private void FitPanes()
    {
        if (_fittingPanes || _body.Width < 440) return;
        _fittingPanes = true;
        try
        {
            _body.Panel2Collapsed = _body.Width < 850 && !_body.Panel1Collapsed;
            _captionRow.Height = Font.Height + 10;
            _statusRow.Height = Font.Height + Status.Margin.Vertical + 10;
            if (_toggleSettings is not null) _toggleSettings.Text = _body.Panel1Collapsed ? "条件を表示" : _body.Panel2Collapsed ? "結果を表示" : "条件を隠す";
            if (_body.Panel1Collapsed || _body.Panel2Collapsed) return;
            var desired = Math.Clamp((int)(300 * DeviceDpi / 96f), 270, 420);
            _body.SplitterDistance = Math.Clamp(desired, _body.Panel1MinSize, Math.Max(_body.Panel1MinSize, _body.Width - _body.Panel2MinSize - _body.SplitterWidth));
        }
        finally { _fittingPanes = false; }
    }
    private void ToggleSettings()
    {
        if (_body.Panel1Collapsed) { _body.Panel1Collapsed = false; FitPanes(); }
        else { _body.Panel2Collapsed = false; _body.Panel1Collapsed = true; }
        _toggleSettings.Text = _body.Panel1Collapsed ? "条件を表示" : _body.Width < 850 ? "結果を表示" : "条件を隠す";
    }
    private void ApplySelectedPreset()
    {
        if (_preset.SelectedIndex < 0) return;
        _applyingPreset = true;
        try { _axisA.ApplyPreset((SecurityChartPreset)_preset.SelectedIndex); _axisTabs.SelectedTab = _axisATab; }
        finally { _applyingPreset = false; }
        if (_body.Panel1Collapsed) ToggleSettings();
        OnSettingsChanged();
    }
    private void OnSettingsChanged()
    {
        if (_applyingPreset) return;
        _dirty = true; SyncShapeChoices();
        Status.Text = _result is null ? "必要な列と条件を確認し、［グラフを作成］で集計します。" : "条件を変更しました。表示中のグラフは前回の結果です。［グラフを作成］で反映します。";
        UpdateActions();
    }
    private void SyncShapeChoices()
    {
        var distribution = _result is not null ? ResultIsDistribution : _axisA.IsDistribution;
        if (_shape.Items.Count > 0 && _distributionShapes == distribution) return;
        _syncingShapes = true; _shape.BeginUpdate();
        try
        {
            _distributionShapes = distribution; _shape.Items.Clear();
            _shape.Items.AddRange(distribution ? [new ShapeChoice(4, "円グラフ"), new ShapeChoice(5, "ドーナツ"), new ShapeChoice(1, "横棒")] :
                [new ShapeChoice(0, "折れ線"), new ShapeChoice(1, "棒"), new ShapeChoice(2, "面"), new ShapeChoice(3, "点")]);
            var wanted = distribution ? _distributionShape : _timeShape;
            _shape.SelectedIndex = Math.Max(0, _shape.Items.Cast<ShapeChoice>().ToList().FindIndex(choice => choice.Id == wanted));
        }
        finally { _shape.EndUpdate(); _syncingShapes = false; }
        _showBands.Visible = _showOutliers.Visible = !distribution;
    }
    private void UpdateActions()
    {
        if (_run is null) return;
        _run.Enabled = ActiveAxes.All(axis => axis.IsReady);
        _addAxis.Enabled = _axisB is null && !_axisA.IsDistribution && _axisA.IsReady;
        _addAxis.Visible = _axisB is null; _removeAxis.Visible = _axisB is not null;
        _axisBTab.Enabled = !_axisA.IsDistribution;
        _axisBTab.Text = _axisA.IsDistribution ? "比較 B（未使用）" : "比較 B";
        if (_axisA.IsDistribution && _axisTabs.SelectedTab == _axisBTab) _axisTabs.SelectedTab = _axisATab;
        _comparisonNote.Text = _axisA.IsDistribution ? "内訳は1つの表を全件集計します。上位以外は「その他」にまとめます。" : "比較は最大2系列。調査Aは左、比較Bは右の目盛りです。";
        _bucket.Enabled = !_axisA.IsDistribution; _zone.Enabled = true;
        _export.Enabled = _result is not null && !_dirty;
        _details.Enabled = _result is not null;
        _pager.Visible = _result is not null && !ResultIsDistribution && _result.PointCount > 1000;
        _previous.Enabled = _result is not null && _offset > 0; _next.Enabled = _result is not null && _offset + _points.Count < _result.PointCount;
    }
    private Task AddAxisAsync()
    {
        if (_axisB is not null || _axisA.IsDistribution) return Task.CompletedTask;
        _axisB = new MetricAxisCard(Owner, "比較 B"); _axisB.SetComparisonMode(); _axisB.SettingsChanged += OnSettingsChanged;
        _axisB.RefreshDocuments(_axisA.Document); _axisB.ApplyPreset(SecurityChartPreset.Custom);
        _axisBTab.Controls.Add(_axisB); _axisTabs.TabPages.Insert(1, _axisBTab); _axisTabs.SelectedTab = _axisBTab;
        UiTheme.Apply(_axisB); OnSettingsChanged(); return Task.CompletedTask;
    }
    private Task RemoveAxisAsync()
    {
        if (_axisB is null) return Task.CompletedTask;
        _axisBTab.Controls.Remove(_axisB); _axisB.Dispose(); _axisB = null; _axisTabs.TabPages.Remove(_axisBTab);
        _axisTabs.SelectedTab = _axisATab; OnSettingsChanged(); return Task.CompletedTask;
    }
    private async Task AnalyzeAsync()
    {
        var unit = _bucket.SelectedIndex switch { 0 => TimeBucketUnit.Second, 2 => TimeBucketUnit.Hour, _ => TimeBucketUnit.Minute };
        var axes = ActiveAxes.Select(axis => axis.Options(unit, _zone.SelectedIndex == 1 ? LogTimeZone.Jst : LogTimeZone.Utc)).ToArray();
        MetricAnalysisResult? result = null; IReadOnlyList<MetricPoint>? points = null;
        var completed = await Owner.RunWork("調査条件で集計", token =>
        {
            var service = new MetricAnalysisService(Owner.Workspace); result = service.Analyze(axes, token);
            // After the cache transaction commits, finish the bounded display fetch even if cancellation arrives.
            points = service.ReadPage(result, 0, 1000, CancellationToken.None);
        });
        if (!completed) { RefreshValidity(); return; }
        _result = result; _points = points!; _offset = 0; _dirty = false;
        SyncShapeChoices(); FillGrid(); UpdateLegend(); DrawChart(); _resultTabs.SelectedTab = _graphTab;
        _caption.Text = Describe(result!); _tips.SetToolTip(_caption, _caption.Text);
        Status.Text = (ResultIsDistribution ? "" : PointSummary(result!) + "。 ") + (result!.CacheHit ? "キャッシュを使用。" : "集計完了。") + " " +
            string.Join(" / ", result.AxisSummaries.Select(summary => $"{(summary.Axis == 0 ? "A" : "B")}: 対象{summary.EligibleRows:N0}行、時刻不正{summary.InvalidTimeRows:N0}、値不正{summary.InvalidValueRows:N0}" +
                (ResultIsDistribution ? $"、空欄{summary.BlankValueRows:N0}行を{(result.Axes[summary.Axis].IncludeBlank ? "含む" : "除外")}" : ""))) +
            (ResultIsDistribution ? "。割合は集計対象全体を分母にしています。" : "。ログがない時間帯は0件として補いません。");
        _tips.SetToolTip(Status, ResultDetailsText());
        if (_body.Width < 850 && !_body.Panel1Collapsed) ToggleSettings();
        UpdateActions();
    }
    private static string Describe(MetricAnalysisResult result)
    {
        return string.Join(" / ", result.Axes.Select((axis, index) =>
        {
            var filters = (axis.Range?.Filter is { } first ? new[] { first } : []).Concat(axis.Range?.Filters ?? []);
            var conditions = filters.Select(filter => axis.Document.Columns[filter.Column].HeaderName + " " + (filter.Operator switch { FilterOperator.Equals => "=", FilterOperator.NotEquals => "≠", _ => filter.Operator.ToString() }) + " " + filter.Value)
                .Concat((axis.Range?.NumericFilters ?? []).Select(filter => axis.Document.Columns[filter.Column].HeaderName + " " + ComparisonSymbol(filter.Comparison) + " " + filter.Value + "（数値）"));
            var condition = string.Join("、", conditions);
            var valueName = axis.ValueColumn is { } column ? axis.Document.Columns[column].HeaderName : "アクセス";
            var measure = axis.Mode switch
            {
                MetricMode.Distribution => valueName + $"の内訳 · 上位{axis.TopCount}件とその他",
                MetricMode.Threshold => valueName + " " + (axis.Threshold!.Comparison switch { MetricComparison.Equal => "=", MetricComparison.NotEqual => "≠", MetricComparison.GreaterOrEqual => "≥", MetricComparison.GreaterThan => ">", MetricComparison.LessOrEqual => "≤", _ => "<" }) + " " + axis.Threshold.Value.ToString(CultureInfo.InvariantCulture) + (axis.Measure == MetricMeasure.Rate ? " の割合" : " の件数"),
                MetricMode.Category => valueName + "の分類別" + (axis.Measure == MetricMeasure.Rate ? "割合" : "件数"),
                _ => valueName + (axis.Aggregate switch { MetricAggregate.Count => "件数", MetricAggregate.Mean => "の平均", MetricAggregate.Sum => "の合計", MetricAggregate.Minimum => "の最小", MetricAggregate.Maximum => "の最大", _ => "の各行の値" })
            };
            var interval = axis.Mode == MetricMode.Distribution || axis.Aggregate == MetricAggregate.Samples && axis.Mode == MetricMode.Raw ? "" : axis.Bucket switch { TimeBucketUnit.Second => " · 1秒ごと", TimeBucketUnit.Hour => " · 1時間ごと", _ => " · 1分ごと" };
            var ranges = new List<string>();
            if (axis.Range is { } range)
            {
                if (range.StartRow is not null || range.EndRow is not null) ranges.Add($"行 {range.StartRow?.ToString("N0") ?? "先頭"}～{range.EndRow?.ToString("N0") ?? "末尾"}");
                if (range.StartTime is { } start) ranges.Add($"{start:yyyy-MM-dd HH:mm:ss zzz} 以降");
                if (range.EndTime is { } end) ranges.Add($"{end:yyyy-MM-dd HH:mm:ss zzz} より前");
                if (range.MinimumValue is not null || range.MaximumValue is not null) ranges.Add($"値 {range.MinimumValue?.ToString(CultureInfo.CurrentCulture) ?? "下限なし"}～{range.MaximumValue?.ToString(CultureInfo.CurrentCulture) ?? "上限なし"}");
            }
            return $"{(index == 0 ? "A" : "B")}: {axis.Document.DisplayName} · {measure}" + interval + (condition.Length == 0 ? "" : " · " + condition) + (ranges.Count == 0 ? "" : " · " + string.Join("、", ranges));
        }));
    }
    private static string ComparisonSymbol(MetricComparison comparison) => comparison switch
    {
        MetricComparison.GreaterThan => ">", MetricComparison.GreaterOrEqual => "≥", MetricComparison.LessThan => "<", MetricComparison.LessOrEqual => "≤", MetricComparison.Equal => "=", _ => "≠"
    };
    private static string PointSummary(MetricAnalysisResult result)
    {
        var points = result.SeriesSummaries.Sum(series => series.PointCount);
        var excluded = result.SeriesSummaries.Sum(series => series.ExcludedPointCount);
        var outliers = result.Axes.All(axis => axis.Outliers == MetricOutlierPolicy.None) ? "外れ値判定なし" : $"外れ値 {result.SeriesSummaries.Sum(series => series.OutlierCount):N0}点";
        return $"集計点 {points:N0}点／{outliers}／表示から除外 {excluded:N0}点";
    }
    private string ResultDetailsText()
    {
        if (_result is not { } result) return "まだ集計していません。";
        var text = new StringBuilder();
        if (_dirty) text.AppendLine("条件は未反映です。以下は前回実行した条件と結果です。").AppendLine();
        text.AppendLine("実行した調査条件").AppendLine(Describe(result)).AppendLine();
        if (ResultIsDistribution) text.AppendLine("項目別内訳では外れ値判定を行いません。");
        else
        {
            text.AppendLine(PointSummary(result));
            text.AppendLine("集計点は各系列の除外前の点数です。元ログの行数とは異なります。").AppendLine();
            foreach (var series in result.SeriesSummaries)
            {
                var axis = result.Axes[series.Axis];
                var rate = axis.Mode != MetricMode.Raw && axis.Measure == MetricMeasure.Rate;
                text.AppendLine($"{(series.Axis == 0 ? "A" : "B")}: {series.Label}");
                text.AppendLine($"集計点 {series.PointCount:N0}点／" + (axis.Outliers == MetricOutlierPolicy.None ? "外れ値判定なし" : $"外れ値 {series.OutlierCount:N0}点") + $"／表示から除外 {series.ExcludedPointCount:N0}点");
                if (axis.Outliers == MetricOutlierPolicy.None) text.AppendLine("外れ値判定なし（すべての集計点を保持）。");
                else
                {
                    text.AppendLine($"判定基準：除外前の平均 ± {axis.Sigma}σ（母集団標準偏差）。除外後も基準を再計算しません。");
                    text.AppendLine($"平均 {Value(series.Mean, rate)}／標準偏差 {Value(series.StandardDeviation, rate)}／下限 {Value(series.BandLow, rate)}／上限 {Value(series.BandHigh, rate)}");
                    text.AppendLine(axis.Outliers == MetricOutlierPolicy.Exclude ? "外れ値の点をグラフ・集計表・CSVから除外。" : "外れ値を保持してマーク。");
                }
                text.AppendLine();
            }
        }
        foreach (var summary in result.AxisSummaries)
        {
            var axis = result.Axes[summary.Axis];
            text.AppendLine($"{(summary.Axis == 0 ? "A" : "B")}: 対象 {summary.EligibleRows:N0}行／時刻不正 {summary.InvalidTimeRows:N0}行／値不正 {summary.InvalidValueRows:N0}行");
            if (axis.Mode == MetricMode.Distribution) text.AppendLine($"空欄 {summary.BlankValueRows:N0}行を{(axis.IncludeBlank ? "含む" : "除外")}。割合の分母は集計対象全体です。");
            if (axis.Range?.NumericFilters?.Count > 0) text.AppendLine("数値比較では列型によらず有限数値を比較し、比較列の空欄・非数値は条件不一致として集計対象から除きます。");
        }
        return text.ToString();
    }
    private Task ShowResultDetailsAsync()
    {
        RefreshValidity(); if (_result is null) return Task.CompletedTask;
        using var form = new Form { Text = "集計結果の詳細", ClientSize = new Size(760, 560), MinimumSize = new Size(500, 340), StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false, MinimizeBox = false, AutoScaleMode = AutoScaleMode.Dpi, Padding = new Padding(16) };
        var content = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = true, ScrollBars = ScrollBars.Vertical, MaxLength = 0, Text = ResultDetailsText(), AccessibleName = "集計結果と外れ値の判定基準" };
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
        var close = new Button { Text = "閉じる", AutoSize = true, MinimumSize = new Size(100, 34), DialogResult = DialogResult.OK };
        footer.Controls.Add(close); form.Controls.Add(content); form.Controls.Add(footer); form.AcceptButton = form.CancelButton = close;
        UiTheme.ThemeForm(form); form.Shown += (_, _) => { content.Select(0, 0); content.ScrollToCaret(); close.Focus(); }; form.ShowDialog(Owner);
        return Task.CompletedTask;
    }
    private async Task LoadPageAsync(long offset)
    {
        if (_result is null || ResultIsDistribution) return;
        IReadOnlyList<MetricPoint>? points = null; var result = _result;
        if (!await Owner.RunWork("取得済み結果を表示", token => points = new MetricAnalysisService(Owner.Workspace).ReadPage(result, offset, 1000, token), canCancel: false)) { RefreshValidity(); return; }
        _offset = offset; _points = points!; FillGrid(); DrawChart(); UpdateActions();
    }
    private void FillGrid()
    {
        _grid.SuspendLayout();
        try
        {
            _grid.Rows.Clear(); _grid.Columns.Clear();
            if (_result is null) return;
            var names = ResultIsDistribution ? new[] { "項目", "件数", "全体に占める割合", "集計対象の総数" } : new[] { "時刻", "系列", "値", "対象数", "一致数", "平均", "標準偏差", "下限", "上限", "外れ値" };
            foreach (var name in names) _grid.Columns.Add("c" + _grid.Columns.Count, name);
            foreach (var point in _points)
            {
                if (ResultIsDistribution) _grid.Rows.Add(point.Label.Length == 0 ? "（空欄）" : point.Label, point.MatchedCount, point.Count == 0 ? "—" : ((double)point.MatchedCount / point.Count).ToString("P2", CultureInfo.CurrentCulture), point.Count);
                else
                {
                    var rate = _result.Axes[point.Axis].Mode != MetricMode.Raw && _result.Axes[point.Axis].Measure == MetricMeasure.Rate;
                    _grid.Rows.Add(point.Time, (point.Axis == 0 ? "A: " : "B: ") + point.Label, Value(point.Value, rate), point.Count, point.MatchedCount, Value(point.Mean, rate), Value(point.StandardDeviation, rate), Value(point.BandLow, rate), Value(point.BandHigh, rate), point.OutlierCount > 0 ? "範囲外" : "");
                }
            }
            _position.Text = $"{(_points.Count == 0 ? 0 : _offset + 1):N0}–{_offset + _points.Count:N0} / {_result.PointCount:N0}点";
        }
        finally { _grid.ResumeLayout(); }
    }
    private static string Value(double? value, bool rate)
    {
        if (value is null) return "—"; var number = value.Value * (rate ? 100 : 1); var magnitude = Math.Abs(number);
        return number.ToString(magnitude >= 1e9 || magnitude is > 0 and < .000001 ? "G10" : "0.######", CultureInfo.CurrentCulture) + (rate ? "%" : "");
    }
    private void DrawChart()
    {
        RefreshValidity();
        _chart.SetMetrics(_result, _points, SelectedShape, _showBands.Checked, _showOutliers.Checked);
    }
    private void UpdateLegend()
    {
        _legend.SuspendLayout();
        try
        {
            foreach (Control label in _legend.Controls.Cast<Control>().ToArray()) label.Dispose();
            _legend.Visible = _result is not null && !ResultIsDistribution && _result.SeriesSummaries.Count > 1;
            if (_result is null || ResultIsDistribution) return;
            foreach (var series in _result.SeriesSummaries)
            {
                var label = new Label { AutoSize = true, MaximumSize = new Size(280, 0), Margin = new Padding(0, 2, 15, 2), ForeColor = LogSeriesChart.SeriesColor(_result, series.Axis, series.Label), Tag = (series.Axis, series.Label), Text = $"● {(series.Axis == 0 ? "A" : "B")}: {series.Label}" };
                _tips.SetToolTip(label, $"外れ値 {series.OutlierCount:N0}点 · 除外 {series.ExcludedPointCount:N0}点"); _legend.Controls.Add(label);
            }
        }
        finally { _legend.ResumeLayout(); }
    }
    internal void RefreshValidity()
    {
        if (_result is null || new MetricAnalysisService(Owner.Workspace).IsCurrent(_result)) return;
        _result = null; _points = []; _grid.Rows.Clear(); _chart.SetMetrics(null, [], 0, false, false); _position.Text = _caption.Text = ""; UpdateLegend(); SyncShapeChoices();
        Status.Text = "元データの変更またはキャッシュ更新で結果を無効にしました。［グラフを作成］で再取得してください。"; UpdateActions();
    }
    internal override void RefreshDocuments(CsvDocument? selected = null)
    {
        var previous = ConfiguredAxes.Select(axis => axis.Document).ToArray();
        foreach (var axis in ConfiguredAxes) axis.RefreshDocuments(selected);
        if (!previous.SequenceEqual(ConfiguredAxes.Select(axis => axis.Document))) OnSettingsChanged();
        RefreshValidity(); UpdateActions();
    }
    private async Task ExportAsync()
    {
        if (_result is null || _dirty) throw new InvalidOperationException("現在の条件で［グラフを作成］してから保存してください。");
        using var dialog = new SaveFileDialog { Filter = "CSV|*.csv|gzip圧縮CSV|*.csv.gz", DefaultExt = "csv", FileName = "security-chart.csv" };
        if (dialog.ShowDialog(Owner) != DialogResult.OK) return;
        var defaults = new CsvDocument { TableName = "", DisplayName = "security-chart.csv", EncodingName = Owner.Settings.DefaultSaveEncodingName, Delimiter = Owner.Settings.DefaultDelimiter, HasHeader = true };
        var options = Dialogs.Csv(Owner, saving: true, document: defaults, writeBom: Owner.Settings.DefaultWriteBom);
        if (options is null) return; var result = _result;
        await Owner.RunWork("集計結果を保存", token => new MetricAnalysisService(Owner.Workspace).ExportCsv(result, dialog.FileName, options, token));
    }
    internal override void ApplyAppearance()
    {
        base.ApplyAppearance(); foreach (var axis in ConfiguredAxes) UiTheme.Apply(axis);
        if (_result is not null) foreach (Control label in _legend.Controls)
            if (label.Tag is ValueTuple<int, string> series) label.ForeColor = LogSeriesChart.SeriesColor(_result, series.Item1, series.Item2);
        _chart.Invalidate();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tips.Dispose(); if (_axisBTab.Parent is null) _axisBTab.Dispose(); }
        base.Dispose(disposing);
    }
}
