using System.Globalization;
using WinCSVTool.Core;

namespace WinCSVTool;

internal enum SecurityChartPreset { Http404, CountryShare, CountryInclude, CountryExclude, Custom }

/// <summary>Edits one series without inspecting any source rows. The surrounding workspace keeps a fixed chart area.</summary>
internal sealed class MetricAxisCard : UserControl
{
    private sealed record SourceChoice(CsvDocument Document) { public override string ToString() => Document.DisplayName + " / " + Document.TableName; }
    private readonly MainForm _owner;
    private readonly ComboBox _sheet = Choice(), _timeColumn = Choice(), _valueColumn = Choice(), _mode = Choice(), _aggregate = Choice(), _measure = Choice(), _comparison = Choice(), _match = Choice(), _outliers = Choice(), _sigma = Choice(), _inputKind = Choice(), _inputZone = Choice();
    private readonly TextBox _name = Entry(), _threshold = Entry("400"), _mapping = Entry(), _startRow = Entry(), _endRow = Entry(), _startTime = Entry(), _endTime = Entry(), _minimum = Entry(), _maximum = Entry(), _custom = Entry();
    private readonly ComboBox[] _filterColumns = [Choice(), Choice()], _filterOperators = [Choice(), Choice()];
    private readonly TextBox[] _filterValues = [Entry(), Entry()];
    private readonly CheckBox _useRange = Check("行・数値の範囲を指定"), _other = Check("未割当を「その他」に含める"), _includeBlank = Check("空欄も1つの分類に含める");
    private readonly NumericUpDown _topCount = new() { Minimum = 1, Maximum = 20, Value = 10, Dock = DockStyle.Fill };
    private readonly Panel _modeHost = new() { Dock = DockStyle.Fill };
    private readonly FeatureTabs _settingsTabs = new() { Dock = DockStyle.Fill };
    private readonly SettingsPage _basic = new(), _filters = new(), _details = new();
    private readonly SettingsPage _rawFields = new(false), _thresholdFields = new(false), _categoryFields = new(false), _distributionFields = new(false);
    private readonly Label _state = new() { Dock = DockStyle.Bottom, Height = 36, AutoEllipsis = true, Padding = new Padding(10, 3, 10, 3) };
    private readonly Label _presetHint = Hint("設定を選び、［グラフを作成］で集計します。"), _filterHint = Hint("空欄の条件は使いません。2条件は両方を満たす行を対象にします。"), _timeLabel = new(), _valueLabel = new(), _statHint = Hint("σは集計点の母集団標準偏差です。外れ値は平均±Nσの外側の点で、除外前の基準を固定します。");
    private Control? _customField, _sigmaField, _outlierField, _measureField, _distributionLimitField;
    private readonly Label _mappingHint = Hint("最大32対応・8分類。同じ表示名は合算します。「分類名を含む」はカンマ・セミコロン区切りです。割合の分母は未割当も含みます。"), _distributionHint = Hint("内訳では上位以外を「その他」にまとめます。空欄を含めない場合は分母からも外します。");
    private CsvDocument? _boundDocument;
    private string[] _columnNames = [];
    private bool _refreshing, _updating, _comparisonOnly;
    private SecurityChartPreset _preset = SecurityChartPreset.Custom;
    internal event Action? SettingsChanged;
    internal CsvDocument? Document => (_sheet.SelectedItem as SourceChoice)?.Document;
    internal bool IsDistribution => _mode.SelectedIndex == 3;
    internal bool IsReady => Document is not null && (IsDistribution || _timeColumn.SelectedIndex > 0) && _valueColumn.SelectedIndex > 0 &&
        (!IsDistribution || _valueColumn.SelectedIndex > 1) && (_mode.SelectedIndex != 1 || !string.IsNullOrWhiteSpace(_threshold.Text)) &&
        (_mode.SelectedIndex != 2 || !string.IsNullOrWhiteSpace(_mapping.Text)) && FiltersReady;
    private bool FiltersReady => (!_presetNeedsFilter || _filterColumns[0].SelectedIndex > 0) && Enumerable.Range(0, 2).All(i =>
        _filterColumns[i].SelectedIndex <= 0 || IsUnary(i) || (IsNumericFilter(i) ? IsFiniteNumber(_filterValues[i].Text) : !string.IsNullOrWhiteSpace(_filterValues[i].Text)));
    private bool _presetNeedsFilter => _preset is SecurityChartPreset.Http404 or SecurityChartPreset.CountryInclude or SecurityChartPreset.CountryExclude;

    internal MetricAxisCard(MainForm owner, string axisName)
    {
        _owner = owner; Dock = DockStyle.Fill; AutoSize = false; MinimumSize = new Size(240, 180); AccessibleName = axisName;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SuspendLayout();
        var pages = new[] { _basic, _filters, _details }; var names = new[] { "基本", "絞り込み", "詳細" };
        for (var i = 0; i < 3; i++)
        {
            var page = new TabPage(names[i]) { Padding = Padding.Empty, UseVisualStyleBackColor = false };
            page.Controls.Add(pages[i]); _settingsTabs.TabPages.Add(page);
        }
        Controls.Add(_settingsTabs); Controls.Add(_state);

        _basic.Add(_presetHint, 30); _basic.Field("対象シート", _sheet);
        _basic.Field("時刻の列", _timeColumn, titleLabel: _timeLabel); _basic.Field("表示・集計する列", _valueColumn, titleLabel: _valueLabel);
        _mode.Items.AddRange(["件数・数値の推移", "しきい値の件数・割合", "値を分類して比較", "列の値ごとの内訳"]); _mode.SelectedIndex = 0;
        _basic.Field("調べる内容", _mode); _basic.Add(_modeHost, 146);
        _aggregate.Items.AddRange(["各行の値（集計なし）", "平均", "合計", "最小", "最大", "件数"]); _aggregate.SelectedIndex = 5;
        _rawFields.Field("集計方法", _aggregate); _rawFields.Add(Hint("アクセス件数を調べる場合は、対象列の「行数（数える）」を選びます。"), 68);
        _comparison.Items.AddRange(["より大きい (>)", "以上 (>=)", "より小さい (<)", "以下 (<=)", "等しい (=)", "等しくない (!=)"]); _comparison.SelectedIndex = 1;
        _thresholdFields.Field("比較方法", _comparison); _thresholdFields.Field("しきい値", _threshold);
        _match.Items.AddRange(["値が完全一致", "分類名を含む（区切り単位）"]); _match.SelectedIndex = 0;
        _mapping.Multiline = true; _mapping.AcceptsReturn = true; _mapping.ScrollBars = ScrollBars.Vertical;
        _mapping.PlaceholderText = "200 => 成功\r\n403 => 拒否\r\n500 => エラー";
        _categoryFields.Field("分類（値 => 表示名）", _mapping, 88); _categoryFields.Field("一致方法", _match);
        _distributionFields.Add(Hint("列の値ごとにアクセス件数を数えます。時刻列は期間で絞る場合だけ使います。"), 70);
        _distributionFields.Add(Hint("円・ドーナツ・横棒で比較できます。"), 40);
        _modeHost.Controls.AddRange([_distributionFields, _categoryFields, _thresholdFields, _rawFields]);
        _name.PlaceholderText = "空欄ならシート名・列名を使用";

        _filters.Add(_filterHint, 72);
        var operatorNames = new object[] { "等しい", "等しくない", "文字を含む", "前方一致", "後方一致", "より大きい（数値）", "より小さい（数値）", "空欄", "空欄ではない", "正規表現" };
        for (var i = 0; i < 2; i++)
        {
            _filterOperators[i].Items.AddRange(operatorNames); _filterOperators[i].SelectedIndex = 0;
            _filters.Field(i == 0 ? "条件 1 · 対象列" : "条件 2 · 対象列（AND）", _filterColumns[i]);
            _filters.Field("条件", _filterOperators[i]); _filters.Field("比較する値", _filterValues[i]);
            _filterValues[i].PlaceholderText = "例: JP / Japan / 日本 / 404";
        }
        _filters.Add(Hint("国の値は元の表の表記に合わせます（例: JP／日本）。数値の大小比較は列の型によらず数値として比較し、空欄・非数値の行を除きます。"), 100);
        _startTime.PlaceholderText = "例: 2026-10-01T00:00:00+09:00"; _endTime.PlaceholderText = "終了時刻は含みません";
        _filters.Field("開始日時（任意・含む）", _startTime); _filters.Field("終了日時（任意・含まない）", _endTime);
        _filters.Add(Hint("日時の指定は時刻列の選択が必要です。空欄なら全期間を対象にします。"), 56);

        _details.Field("系列名（任意）", _name);
        _measure.Items.AddRange(["件数", "割合 (%)"]); _measure.SelectedIndex = 0;
        _measureField = _details.Field("しきい値・分類の表示値", _measure); _details.Add(_other, 36); _details.Add(_mappingHint, 66);
        _distributionLimitField = _details.Field("内訳の上位件数", _topCount); _details.Add(_includeBlank, 36); _details.Add(_distributionHint, 66);
        _sigma.Items.AddRange(["1σ", "2σ", "3σ"]); _sigma.SelectedIndex = 1;
        _outliers.Items.AddRange(["含める", "含めてマークする", "外れ値の点を除外する"]); _outliers.SelectedIndex = 1;
        _sigmaField = _details.Field("標準偏差の範囲", _sigma); _outlierField = _details.Field("外れ値の扱い", _outliers); _details.Add(_statHint, 92);
        _inputKind.Items.AddRange(["自動判定", "UNIX秒", "UNIXミリ秒", "ISO 8601", "Apache形式", "カスタム書式"]); _inputKind.SelectedIndex = 0;
        _inputZone.Items.AddRange(["JST (+09:00)", "UTC (+00:00)"]); _inputZone.SelectedIndex = 0;
        _details.Field("入力の時刻形式", _inputKind); _details.Field("時差なし入力の基準", _inputZone); _customField = _details.Field("入力のカスタム書式", _custom);
        _details.Add(_useRange, 38); _startRow.PlaceholderText = "先頭から"; _endRow.PlaceholderText = "最後まで";
        _details.Field("開始行（1始まり・含む）", _startRow); _details.Field("終了行（含む）", _endRow);
        _details.Field("数値の下限（任意）", _minimum); _details.Field("数値の上限（任意）", _maximum);
        _details.Add(Hint("行範囲は元の表のデータ行順です。表のソート・フィルターとは独立します。数値範囲は上下限を含みます。"), 66);

        // The workspace chooses its initial template before documents are bound.
        _sheet.Items.Add("選択してください"); _sheet.SelectedIndex = 0; RefreshColumns();

        foreach (var combo in new[] { _timeColumn, _valueColumn, _mode, _aggregate, _measure, _comparison, _match, _outliers, _sigma, _inputKind, _inputZone }.Concat(_filterColumns).Concat(_filterOperators)) combo.SelectedIndexChanged += (_, _) => Changed();
        foreach (var text in new[] { _name, _threshold, _mapping, _startRow, _endRow, _startTime, _endTime, _minimum, _maximum, _custom }.Concat(_filterValues)) text.TextChanged += (_, _) => Changed();
        foreach (var check in new[] { _useRange, _other, _includeBlank }) check.CheckedChanged += (_, _) => Changed();
        _topCount.ValueChanged += (_, _) => Changed();
        _sheet.SelectedIndexChanged += (_, _) =>
        {
            if (_refreshing || _updating) return;
            _updating = true;
            try { RefreshColumns(); ConfigurePreset(); }
            finally { _updating = false; }
            Changed();
        };
        ResumeLayout(true); SelectPage(0); UpdateState();
    }

    private static ComboBox Choice() => new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, MinimumSize = Size.Empty, IntegralHeight = true, DropDownWidth = 280 };
    private static TextBox Entry(string text = "") => new() { Text = text, Dock = DockStyle.Fill };
    private static CheckBox Check(string text) => new() { Text = text, Dock = DockStyle.Fill, AutoSize = false };
    private static Label Hint(string text) => new() { Text = text, Dock = DockStyle.Fill, Padding = new Padding(0, 3, 0, 3) };
    private void SelectPage(int index)
    {
        _settingsTabs.SelectedIndex = index;
    }

    private void Changed()
    {
        if (_updating || _refreshing) return;
        UpdateState(); SettingsChanged?.Invoke();
    }

    /// <summary>The fixed editor no longer collapses or resizes when analysis finishes.</summary>
    internal void SetCollapsed(bool collapsed) { }

    private void UpdateState()
    {
        if (_updating) return; _updating = true;
        try
        {
            var distribution = IsDistribution; var count = _valueColumn.SelectedIndex == 1;
            if (count && !distribution) { _aggregate.SelectedIndex = (int)MetricAggregate.Count; _mode.SelectedIndex = 0; }
            _timeColumn.Enabled = Document is not null; _timeLabel.Text = distribution ? "期間で絞る時刻の列（任意）" : "時刻の列";
            _valueLabel.Text = distribution ? "内訳にする列（国・IPなど）" : "表示・集計する列";
            _aggregate.Enabled = !count;
            var pages = new[] { _rawFields, _thresholdFields, _categoryFields, _distributionFields };
            for (var i = 0; i < pages.Length; i++) pages[i].Visible = _mode.SelectedIndex == i;
            pages[Math.Max(0, _mode.SelectedIndex)].BringToFront();
            _measure.Enabled = _mode.SelectedIndex is 1 or 2; _other.Enabled = _mode.SelectedIndex == 2;
            _topCount.Enabled = _includeBlank.Enabled = distribution;
            _details.ShowRow(_measureField!, _mode.SelectedIndex is 1 or 2); _details.ShowRow(_other, _mode.SelectedIndex == 2); _details.ShowRow(_mappingHint, _mode.SelectedIndex == 2);
            _details.ShowRow(_distributionLimitField!, distribution); _details.ShowRow(_includeBlank, distribution); _details.ShowRow(_distributionHint, distribution);
            _details.ShowRow(_sigmaField!, !distribution); _details.ShowRow(_outlierField!, !distribution); _details.ShowRow(_statHint, !distribution);
            _inputKind.Enabled = _inputZone.Enabled = !distribution || _timeColumn.SelectedIndex > 0;
            _customField!.Enabled = _inputKind.Enabled && _inputKind.SelectedIndex == 5;
            _startRow.Enabled = _endRow.Enabled = _useRange.Checked;
            _minimum.Enabled = _maximum.Enabled = _useRange.Checked && !count && !distribution;
            for (var i = 0; i < 2; i++) { _filterOperators[i].Enabled = _filterColumns[i].SelectedIndex > 0; _filterValues[i].Enabled = _filterColumns[i].SelectedIndex > 0 && !IsUnary(i); }
            _state.Text = Document is null ? "対象シートを選択" : !IsDistribution && _timeColumn.SelectedIndex <= 0 ? "時刻の列を選択" : _valueColumn.SelectedIndex <= 0 || distribution && count ? "集計に使う列を選択" : HasInvalidNumericFilter ? "比較値は有限の数値で入力" : !FiltersReady ? "［絞り込み］の条件を確認" : IsReady ? "準備完了 · ［グラフを作成］" : "必要な値や分類を入力";
        }
        finally { _updating = false; }
    }

    private bool IsUnary(int index) => _filterOperators[index].SelectedIndex is 7 or 8;
    private bool IsNumericFilter(int index) => _filterOperators[index].SelectedIndex is 5 or 6;
    private static bool IsFiniteNumber(string value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number);
    private bool HasInvalidNumericFilter => Enumerable.Range(0, 2).Any(i => _filterColumns[i].SelectedIndex > 0 && IsNumericFilter(i) && !IsFiniteNumber(_filterValues[i].Text));

    internal void ApplyPreset(SecurityChartPreset preset)
    {
        if (_comparisonOnly && preset == SecurityChartPreset.CountryShare) throw new InvalidOperationException("項目別内訳は調査Aの1軸で表示します。");
        _updating = true;
        try { _preset = preset; ConfigurePreset(); SelectPage(0); }
        finally { _updating = false; }
        Changed();
    }

    internal void SetComparisonMode()
    {
        _updating = true;
        try
        {
            _comparisonOnly = true;
            if (IsDistribution) { _mode.SelectedIndex = 0; _preset = SecurityChartPreset.Custom; }
            if (_mode.Items.Count > 3) _mode.Items.RemoveAt(3);
        }
        finally { _updating = false; }
        Changed();
    }

    private void ConfigurePreset()
    {
        _presetHint.Text = _preset switch
        {
            SecurityChartPreset.Http404 => "HTTP 404の件数を時間ごとに比較します。",
            SecurityChartPreset.CountryShare => "国コード・国名の列でアクセス比率を比較します。",
            SecurityChartPreset.CountryInclude => "国の値を［絞り込み］で入力します。",
            SecurityChartPreset.CountryExclude => "除外する国を［絞り込み］で入力します。",
            _ => "対象と調べる内容を選びます。条件や詳細は各タブで設定します。"
        };
        _timeColumn.SelectedIndex = _preset == SecurityChartPreset.CountryShare ? 0 : GuessColumn(TimeAliases) + 1;
        _valueColumn.SelectedIndex = _preset == SecurityChartPreset.CountryShare ? Math.Max(0, GuessColumn(CountryAliases) + 2) : 1;
        if (_preset == SecurityChartPreset.CountryShare && _valueColumn.SelectedIndex == 1) _valueColumn.SelectedIndex = 0;
        _mode.SelectedIndex = _preset == SecurityChartPreset.CountryShare ? 3 : 0; _aggregate.SelectedIndex = (int)MetricAggregate.Count;
        for (var i = 0; i < 2; i++) { _filterColumns[i].SelectedIndex = 0; _filterOperators[i].SelectedIndex = 0; _filterValues[i].Clear(); }
        if (_presetNeedsFilter)
        {
            _filterColumns[0].SelectedIndex = GuessColumn(_preset == SecurityChartPreset.Http404 ? StatusAliases : CountryAliases) + 1;
            _filterOperators[0].SelectedIndex = _preset == SecurityChartPreset.CountryExclude ? 1 : 0;
            _filterValues[0].Text = _preset == SecurityChartPreset.Http404 ? "404" : "";
        }
        _filterHint.Text = _presetNeedsFilter ? "条件1は必須、条件2は任意です。両方を満たす行を集計します。" : "指定した条件をすべて満たす行を集計します。対象列が未選択の条件は使いません。";
        _name.Clear(); _startTime.Clear(); _endTime.Clear(); _useRange.Checked = false;
        _basic.PerformLayout(); _filters.PerformLayout();
    }

    private static readonly string[] TimeAliases = ["timestamp", "@timestamp", "date", "datetime", "time", "time_iso8601", "time_local", "%t", "日時", "時刻", "日付", "requesttime"];
    private static readonly string[] StatusAliases = ["status", "statuscode", "httpstatus", "httpstatuscode", "scstatus", "responsecode", "EdgeResponseStatus", "http.response.status_code", "%s", "%>s", "HTTPステータス", "ステータス"];
    private static readonly string[] CountryAliases = ["country", "countrycode", "countryname", "国", "国コード", "国名", "国・地域（参考）", "Country (reference)", "ipcountry", "geoipcountrycode", "geoipcountryname", "clientcountry", "clientcountrycode", "clientcountryname", "httpRequest.country"];
    private int GuessColumn(string[] aliases)
    {
        if (Document is null) return -1;
        static string Key(string value) => value.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
        var keys = aliases.Select(Key).ToHashSet(StringComparer.Ordinal);
        var matches = Document.Columns.Select((column, index) => (column, index)).Where(item => keys.Contains(Key(item.column.HeaderName))).ToArray();
        return matches.Length == 1 ? matches[0].index : -1;
    }

    internal void RefreshDocuments(CsvDocument? preferred = null)
    {
        var old = Document; _refreshing = true;
        try
        {
            _sheet.BeginUpdate();
            try
            {
                _sheet.Items.Clear(); _sheet.Items.Add("選択してください");
                foreach (var document in _owner.Workspace.Documents) _sheet.Items.Add(new SourceChoice(document));
                var wanted = old is not null && _owner.Workspace.Documents.Contains(old) ? old : preferred;
                var index = _sheet.Items.Cast<object>().ToList().FindIndex(item => item is SourceChoice choice && ReferenceEquals(choice.Document, wanted));
                _sheet.SelectedIndex = Math.Max(0, index);
            }
            finally { _sheet.EndUpdate(); }
            var changed = !ReferenceEquals(_boundDocument, Document); RefreshColumns();
            if (changed) ConfigurePreset();
        }
        finally { _refreshing = false; }
        UpdateState();
        if (!ReferenceEquals(old, Document)) SettingsChanged?.Invoke();
    }

    private void RefreshColumns()
    {
        var same = ReferenceEquals(_boundDocument, Document);
        string? Previous(ComboBox choice, int prefix) => same && choice.SelectedIndex >= prefix && choice.SelectedIndex - prefix < _columnNames.Length ? _columnNames[choice.SelectedIndex - prefix] : null;
        var oldTime = Previous(_timeColumn, 1); var oldValue = Previous(_valueColumn, 2); var count = same && _valueColumn.SelectedIndex == 1;
        var oldFilters = _filterColumns.Select(choice => Previous(choice, 1)).ToArray();
        _columnNames = Document?.Columns.Select(column => column.Name).ToArray() ?? [];
        var headerNames = Document?.Columns.Select(column => (object)column.HeaderName).ToArray() ?? [];
        void Fill(ComboBox choice, object[] prefix, string? previous, int special = -1)
        {
            choice.BeginUpdate();
            try
            {
                choice.Items.Clear(); choice.Items.AddRange(prefix); choice.Items.AddRange(headerNames);
                var index = previous is null ? -1 : Array.IndexOf(_columnNames, previous);
                choice.SelectedIndex = special >= 0 ? special : index < 0 ? 0 : index + prefix.Length;
            }
            finally { choice.EndUpdate(); }
        }
        Fill(_timeColumn, ["選択してください"], oldTime); Fill(_valueColumn, ["選択してください", "行数（数える）"], oldValue, count ? 1 : -1);
        for (var i = 0; i < 2; i++) Fill(_filterColumns[i], ["指定しない"], oldFilters[i]);
        _boundDocument = Document;
    }

    internal MetricAxisOptions Options(TimeBucketUnit bucket, LogTimeZone outputZone)
    {
        if (HasInvalidNumericFilter) throw new InvalidOperationException("数値比較の値は、小数点「.」を使った有限の数値で入力してください。");
        if (!IsReady || Document is not { } document) throw new InvalidOperationException("対象シート・列と絞り込み条件を確認してください。");
        var inputKind = IsDistribution && _timeColumn.SelectedIndex <= 0 ? TimeInputKind.Auto : (TimeInputKind)_inputKind.SelectedIndex;
        var time = new TimeParseOptions(inputKind, _inputZone.SelectedIndex == 1 ? LogTimeZone.Utc : LogTimeZone.Jst, outputZone, inputKind == TimeInputKind.Custom ? _custom.Text : null);
        TimeNormalization.Validate(time);
        var value = _valueColumn.SelectedIndex == 1 ? (int?)null : _valueColumn.SelectedIndex - 2;
        var mode = (MetricMode)_mode.SelectedIndex;
        if (value is null && mode != MetricMode.Raw) throw new InvalidOperationException("しきい値・分類・内訳には、値が入った列を選んでください。");
        var rangeTime = time with { InputKind = TimeInputKind.Auto, CustomFormat = null };
        DateTimeOffset? Date(TextBox box) => string.IsNullOrWhiteSpace(box.Text) ? null : TimeNormalization.TryParse(box.Text, rangeTime, out var date) ? date : throw new InvalidOperationException("範囲の日時をISO 8601またはUNIX時刻で入力してください。");
        long? Row(TextBox box) => string.IsNullOrWhiteSpace(box.Text) ? null : long.TryParse(box.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var row) && row > 0 ? row : throw new InvalidOperationException("行範囲は1以上の整数で入力してください。");
        var operators = new[] { FilterOperator.Equals, FilterOperator.NotEquals, FilterOperator.Contains, FilterOperator.StartsWith, FilterOperator.EndsWith, FilterOperator.GreaterThan, FilterOperator.LessThan, FilterOperator.Empty, FilterOperator.NotEmpty, FilterOperator.Regex };
        var filters = Enumerable.Range(0, 2).Where(i => _filterColumns[i].SelectedIndex > 0 && !IsNumericFilter(i)).Select(i => new FilterRule(_filterColumns[i].SelectedIndex - 1, operators[Math.Max(0, _filterOperators[i].SelectedIndex)], _filterValues[i].Text)).ToArray();
        var numericFilters = Enumerable.Range(0, 2).Where(i => _filterColumns[i].SelectedIndex > 0 && IsNumericFilter(i)).Select(i => new MetricNumericFilter(_filterColumns[i].SelectedIndex - 1, _filterOperators[i].SelectedIndex == 5 ? MetricComparison.GreaterThan : MetricComparison.LessThan, _filterValues[i].Text)).ToArray();
        var start = Date(_startTime); var end = Date(_endTime);
        if ((start.HasValue || end.HasValue) && _timeColumn.SelectedIndex <= 0) throw new InvalidOperationException("期間を絞る場合は［基本］で時刻の列を選んでください。");
        var range = new MetricRangeOptions(_useRange.Checked ? Row(_startRow) : null, _useRange.Checked ? Row(_endRow) : null, start, end,
            _useRange.Checked && value.HasValue && !IsDistribution ? Number(_minimum, true) : null,
            _useRange.Checked && value.HasValue && !IsDistribution ? Number(_maximum, true) : null, Filters: filters, NumericFilters: numericFilters);
        var mappings = new List<MetricCategoryMapping>();
        if (mode == MetricMode.Category)
            foreach (var line in _mapping.Lines.Where(line => !string.IsNullOrWhiteSpace(line)))
            {
                var pieces = line.Split("=>", 2, StringSplitOptions.TrimEntries);
                if (pieces.Length != 2 || pieces[0].Length == 0 || pieces[1].Length == 0) throw new InvalidOperationException("分類は「元の値 => 表示名」を1行ずつ入力してください。");
                mappings.Add(new(pieces[0], pieces[1]));
            }
        var name = _name.Text.Trim();
        if (name.Length == 0)
        {
            name = document.DisplayName + " / " + (value is { } column ? document.Columns[column].HeaderName : "件数");
            if (name.Length > 120) name = name[..(char.IsHighSurrogate(name[119]) && char.IsLowSurrogate(name[120]) ? 119 : 120)];
        }
        return new(document, _timeColumn.SelectedIndex - 1, value, mode, value is null || IsDistribution ? MetricAggregate.Count : (MetricAggregate)_aggregate.SelectedIndex, bucket, time, range,
            mode == MetricMode.Threshold ? new((MetricComparison)_comparison.SelectedIndex, Number(_threshold, false)!.Value) : null, mappings,
            (MetricCategoryMatch)Math.Max(0, _match.SelectedIndex), _other.Checked ? MetricUnmappedPolicy.Other : MetricUnmappedPolicy.Exclude, "その他", IsDistribution ? MetricMeasure.Count : (MetricMeasure)Math.Max(0, _measure.SelectedIndex),
            IsDistribution ? MetricOutlierPolicy.None : (MetricOutlierPolicy)_outliers.SelectedIndex, _sigma.SelectedIndex + 1, name, TopCount: (int)_topCount.Value, IncludeBlank: _includeBlank.Checked, ShowOther: true);
    }

    private static double? Number(TextBox box, bool optional)
    {
        if (optional && string.IsNullOrWhiteSpace(box.Text)) return null;
        return double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : throw new InvalidOperationException("数値は小数点「.」を使った有限の値で入力してください。");
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        if (_state is not null) _state.Height = Font.Height + 10;
    }

    /// <summary>All row positions are fixed for a given font/DPI. Text changes never resize or relayout the workspace.</summary>
    private sealed class SettingsPage : Panel
    {
        private readonly List<(Control Control, int Height)> _rows = [];
        private readonly HashSet<Control> _hidden = [];
        internal SettingsPage(bool scroll = true) { Dock = DockStyle.Fill; AutoScroll = scroll; Padding = new Padding(10, 8, 10, 8); DoubleBuffered = true; }
        internal void Add(Control control, int height) { control.Dock = DockStyle.None; _rows.Add((control, height)); Controls.Add(control); PerformLayout(); }
        internal void ShowRow(Control control, bool visible)
        {
            var changed = visible ? _hidden.Remove(control) : _hidden.Add(control);
            if (!changed) return; control.Visible = visible; PerformLayout();
        }
        internal Control Field(string title, Control input, int height = 46, Label? titleLabel = null)
        {
            var field = new Panel { Margin = Padding.Empty };
            var label = titleLabel ?? new Label(); label.Text = title; label.AutoSize = false; label.Dock = DockStyle.Top; label.Height = 24; label.Padding = new Padding(0, 2, 0, 1);
            input.Dock = DockStyle.Top; field.Controls.Add(input); field.Controls.Add(label);
            field.SizeChanged += (_, _) => { label.Height = Math.Max(22, label.Font.Height + 5); if (input is TextBox { Multiline: true }) input.Height = Math.Max(30, field.Height - label.Height - 5); };
            Add(field, height); return field;
        }
        protected override void OnLayout(LayoutEventArgs levent)
        {
            if (_rows is null) { base.OnLayout(levent); return; }
            var ratio = Math.Max(.8f, Font.Height / 18f); var y = Padding.Top + AutoScrollPosition.Y;
            var active = _rows.Where(row => !_hidden.Contains(row.Control)).ToArray();
            var totalHeight = active.Sum(row => (int)Math.Ceiling(row.Height * ratio)) + Padding.Vertical;
            var width = Math.Max(10, ClientSize.Width - Padding.Horizontal - (AutoScroll && totalHeight > ClientSize.Height && !VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0));
            int Height((Control Control, int Height) row) => Math.Max((int)Math.Ceiling(row.Height * ratio), row.Control is Label label ? TextRenderer.MeasureText(label.Text, label.Font, new Size(Math.Max(10, width - label.Padding.Horizontal), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height + label.Padding.Vertical + 4 : 0);
            totalHeight = active.Sum(Height) + Padding.Vertical;
            foreach (var row in active) { var height = Height(row); row.Control.SetBounds(Padding.Left, y, width, height); y += height; }
            if (AutoScroll) AutoScrollMinSize = new Size(0, totalHeight);
            // Scroll extents must be measured after old, wider child bounds have been replaced.
            base.OnLayout(levent);
        }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); PerformLayout(); }
    }

}
