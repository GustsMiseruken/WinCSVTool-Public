using WinCSVTool.Core;

namespace WinCSVTool;

internal sealed class LogTimeFields
{
    private readonly ComboBox _kind = LogFeaturePage.Choice();
    private readonly ComboBox _inputZone = LogFeaturePage.Choice();
    private readonly ComboBox _outputZone = LogFeaturePage.Choice();
    private readonly ComboBox _outputKind = LogFeaturePage.Choice();
    private readonly TextBox _custom = new() { Dock = DockStyle.Fill, PlaceholderText = "例: yyyy-MM-dd HH:mm:ss" };
    private readonly Label _unixHint = new() { AutoSize = true, Dock = DockStyle.Fill, Text = "UNIX時刻は1970-01-01 00:00:00 UTCからの経過時間です。出力タイムゾーンには依存しません。" };
    private readonly bool _chooseOutputKind;
    private static readonly string[] KindNames = ["自動判定", "UNIX 秒", "UNIX ミリ秒", "ISO 8601", "アクセスログ日時 (Apache形式)", "日時書式を指定"];
    internal LogTimeFields(Action<string, Control> add, bool chooseOutputKind = true)
    {
        _chooseOutputKind = chooseOutputKind;
        _kind.Items.AddRange(KindNames); _kind.SelectedIndex = 0;
        _inputZone.Items.AddRange(["JST (+09:00)", "UTC (+00:00)"]); _inputZone.SelectedIndex = 0;
        _outputZone.Items.AddRange(["UTC (+00:00)", "JST (+09:00)"]); _outputZone.SelectedIndex = 0;
        _outputKind.Items.AddRange(["ISO 8601", "UNIX 秒", "UNIX ミリ秒"]); _outputKind.SelectedIndex = 0;
        if (chooseOutputKind)
        {
            add("出力の時刻形式", _outputKind); add("出力のタイムゾーン", _outputZone); add("UNIX時刻について", _unixHint);
        }
        add("入力の時刻形式", _kind); add("時差のない入力のタイムゾーン", _inputZone); add("カスタム日時書式", _custom);
        if (!chooseOutputKind) add("集計のタイムゾーン", _outputZone);
        _kind.SelectedIndexChanged += (_, _) => UpdateFields(); _outputKind.SelectedIndexChanged += (_, _) => UpdateFields(); UpdateFields();
    }
    private void UpdateFields()
    {
        SetFieldVisible(_custom, _kind.SelectedIndex == 5);
        SetFieldVisible(_inputZone, _kind.SelectedIndex is not (1 or 2));
        if (!_chooseOutputKind) return;
        var unix = _outputKind.SelectedIndex is 1 or 2;
        SetFieldVisible(_outputZone, !unix); SetFieldVisible(_unixHint, unix);
    }
    private static void SetFieldVisible(Control control, bool visible)
    {
        if (control.Parent is TableLayoutPanel table)
        {
            var row = table.GetRow(control);
            foreach (Control item in table.Controls) if (table.GetRow(item) == row) item.Visible = visible;
        }
        else control.Visible = visible;
    }
    internal TimeParseOptions Options => new((TimeInputKind)Math.Max(0, _kind.SelectedIndex), _inputZone.SelectedIndex == 1 ? LogTimeZone.Utc : LogTimeZone.Jst, _outputZone.SelectedIndex == 1 ? LogTimeZone.Jst : LogTimeZone.Utc, _custom.Text, (TimeOutputKind)Math.Max(0, _outputKind.SelectedIndex));
}

internal sealed class LogTimePage : LogFeaturePage
{
    private readonly ComboBox _column;
    private readonly LogTimeFields _time;
    private readonly TextBox _header = new() { Dock = DockStyle.Fill, Text = "正規化時刻" };
    internal LogTimePage(MainForm owner) : base(owner, "時刻の正規化・変換", "ISO 8601・UNIX秒・ミリ秒に変換し、新しい列へ追加します。元の列は保持します。入力に時差の指定があればそれを優先し、ない日時には入力タイムゾーンを使います。")
    {
        _column = AddColumn("時刻の列"); _time = new LogTimeFields(AddField); AddField("追加する列名", _header);
        AddAction("実行：変換列を追加", ConvertAsync);
        Status.Text = "表全体が対象です。変換できない値は新しい列で空欄になり、実行後に件数を表示します。元の値は保持します。";
    }
    private async Task ConvertAsync()
    {
        var document = RequireDocument(); var column = RequireColumn(_column); var options = _time.Options; TimeNormalization.Validate(options);
        var header = _header.Text.Trim(); if (header.Length == 0) throw new InvalidOperationException("追加する列名を入力してください。");
        if (!Owner.ConfirmUnsupportedUndo($"全{document.RowCount:N0}行への時刻変換列の追加")) return;
        LogTransformSummary? summary = null;
        await Owner.RunWork("時刻を正規化", token => summary = Owner.Workspace.AddNormalizedTimeColumn(document, column, options, header, token));
        if (summary is not null)
        {
            await Owner.RefreshLogColumnsAsync(document);
            Status.Text = $"完了：{summary.ProcessedRows:N0}行を処理、変換できない値 {summary.InvalidRows:N0}行。結果を残すにはCSVまたはExcelへ保存してください。";
        }
    }
}
