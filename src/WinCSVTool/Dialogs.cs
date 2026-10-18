using System.Text.RegularExpressions;
using WinCSVTool.Core;

namespace WinCSVTool;

internal static class Dialogs
{
    public static string? Text(IWin32Window owner, string title, string label, string value = "")
    {
        using var f = Base(title, 520, 190);
        var input = new TextBox { Text = value, Dock = DockStyle.Top };
        f.Controls.Add(Body(new Label { Text = label, AutoSize = true }, input));
        Buttons(f);
        f.Shown += (_, _) => { input.Focus(); input.SelectAll(); };
        return f.ShowDialog(owner) == DialogResult.OK ? input.Text : null;
    }

    public static T? Choice<T>(IWin32Window owner, string title, string label, IReadOnlyList<T> values, int selected = 0, Func<T, string>? display = null) where T : struct
    {
        using var f = Base(title, 480, 190);
        var combo = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var value in values) combo.Items.Add(display is null ? value.ToString()! : display(value));
        combo.SelectedIndex = Math.Clamp(selected, 0, values.Count - 1);
        f.Controls.Add(Body(new Label { Text = label, AutoSize = true }, combo));
        Buttons(f);
        return f.ShowDialog(owner) == DialogResult.OK ? values[combo.SelectedIndex] : null;
    }

    public static CsvOptions? Csv(IWin32Window owner, bool saving, CsvDocument? document = null, bool writeBom = false)
    {
        using var f = Base(saving ? "保存形式" : "CSVを開く — 読み込み設定", 560, saving ? 510 : 410);
        var enc = new ComboBox { Width = 290, DropDownStyle = ComboBoxStyle.DropDownList };
        string[] encodings = saving ? ["utf-8", "shift_jis", "euc-jp", "utf-16", "utf-16BE"] : ["auto", "utf-8", "shift_jis", "euc-jp", "utf-16", "utf-16BE"];
        enc.Items.AddRange(encodings);
        var selectedEncoding = document?.EncodingName ?? (saving ? "utf-8" : "auto");
        if (!selectedEncoding.Equals("auto", StringComparison.OrdinalIgnoreCase))
            selectedEncoding = EncodingDetector.Resolve(selectedEncoding).CodePage switch { 932 => "shift_jis", 51932 => "euc-jp", 1200 => "utf-16", 1201 => "utf-16BE", _ => "utf-8" };
        enc.SelectedIndex = Math.Max(0, Array.FindIndex(encodings, encoding => encoding.Equals(selectedEncoding, StringComparison.OrdinalIgnoreCase)));
        var header = new CheckBox { Text = "先頭レコードをヘッダーとして扱う", Checked = document?.HasHeader ?? true, AutoSize = true };
        var bom = new CheckBox { Text = "BOMを付けて保存する", AutoSize = true, Visible = saving, Checked = writeBom };
        var formulas = new CheckBox { Text = "表計算ソフト向け: 数式としての解釈を抑止", AutoSize = true, Visible = saving, Checked = false };
        var delimiter = new ComboBox { Width = 290, DropDownStyle = ComboBoxStyle.DropDownList };
        delimiter.Items.AddRange(["カンマ (,)", "タブ (TSV)", "セミコロン (;)" ]);
        delimiter.SelectedIndex = document?.Delimiter switch { '\t' => 1, ';' => 2, _ => 0 };
        f.Controls.Add(Body(new Label { Text = "文字コード（自動判定が合わない場合は明示指定）", AutoSize = true }, enc,
            new Label { Text = "区切り文字", AutoSize = true }, delimiter, header, bom, formulas,
            new Label { Text = "有効にすると式に見える値の先頭へ ' を追加します。\n元の値が変わるため、通常のCSV保存では無効にしてください。", AutoSize = true, Visible = saving }));
        Buttons(f);
        return f.ShowDialog(owner) == DialogResult.OK ? new CsvOptions((string)enc.SelectedItem!, header.Checked,
            delimiter.SelectedIndex switch { 1 => '\t', 2 => ';', _ => ',' }, bom.Checked, formulas.Checked) : null;
    }

    public static Form Base(string title, int width, int height)
    {
        var form = new Form
        {
            Text = title, ClientSize = new Size(width, height), StartPosition = FormStartPosition.CenterParent,
            Font = new Font("Yu Gothic UI", 9.5f), FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false, MaximizeBox = false, BackColor = Color.White, AutoScaleMode = AutoScaleMode.Dpi
        };
        UiTheme.ThemeForm(form);
        return form;
    }

    public static FlowLayoutPanel Body(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        foreach (var control in controls) { control.Margin = new Padding(0, 0, 0, 10); panel.Controls.Add(control); }
        // FlowLayoutPanel does not stretch DockStyle.Top inputs. Keep fields and long hints within the dialog.
        void FitFields()
        {
            var width = Math.Max(100, panel.ClientSize.Width - panel.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);
            foreach (Control control in panel.Controls)
            {
                if (control is TextBox or ComboBox or FlowLayoutPanel) control.Width = width;
                if (control is Label { AutoSize: true } label) label.MaximumSize = new Size(width, 0);
                if (control is CheckBox { AutoSize: true } checkBox) checkBox.MaximumSize = new Size(width, 0);
            }
        }
        panel.SizeChanged += (_, _) => FitFields();
        FitFields();
        return panel;
    }

    public static void Buttons(Form form)
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 64, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(18, 12, 18, 10) };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(110, 34), Margin = new Padding(6, 0, 0, 0) };
        var ok = new Button { Text = "適用", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(110, 34), Margin = new Padding(6, 0, 0, 0) };
        panel.Controls.AddRange([cancel, ok]); form.Controls.Add(panel); panel.SendToBack();
        form.AcceptButton = ok; form.CancelButton = cancel;
        UiTheme.ThemeForm(form);
        panel.BackColor = UiTheme.Header;
    }
}

internal sealed class ViewDialog : Form
{
    private readonly ComboBox _column = new() { Width = 195, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _operator = new() { Width = 135, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _value = new() { Width = 195, PlaceholderText = "比較する値" };
    private readonly ListBox _filters = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true, IntegralHeight = false };
    private readonly ListBox _sorts = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true, IntegralHeight = false };
    public ViewSpec Result { get; } = new();

    public ViewDialog(CsvDocument doc, ViewSpec current)
    {
        Text = "ソートとフィルター"; ClientSize = new Size(800, 530); StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Yu Gothic UI", 9.5f); BackColor = Color.White; MinimizeBox = false; MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog; AutoScaleMode = AutoScaleMode.Dpi;
        for (var i = 0; i < doc.Columns.Count; i++) _column.Items.Add($"{i + 1}: {doc.Columns[i].HeaderName}");
        if (_column.Items.Count > 0) _column.SelectedIndex = 0;
        _operator.Items.AddRange(["含む", "等しい", "等しくない", "で始まる", "で終わる", "より大きい", "より小さい", "空欄", "空欄以外", "正規表現"]);
        _operator.SelectedIndex = 0;
        Result.Filters.AddRange(current.Filters); Result.Sorts.AddRange(current.Sorts);
        var addFilter = new Button { Text = "条件を追加", AutoSize = true };
        addFilter.Click += (_, _) =>
        {
            if (_column.SelectedIndex < 0) return;
            if ((FilterOperator)_operator.SelectedIndex == FilterOperator.Regex)
            {
                try { _ = new Regex(_value.Text, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); }
                catch (ArgumentException ex) { MessageBox.Show(this, ex.Message, "正規表現が不正です", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            }
            Result.Filters.Add(new FilterRule(_column.SelectedIndex, (FilterOperator)_operator.SelectedIndex, _value.Text)); RefreshRules(doc);
        };
        var filterLine = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 54, Padding = new Padding(0, 8, 0, 8), WrapContents = false };
        filterLine.Controls.AddRange([_column, _operator, _value, addFilter]);
        var filterGroup = new GroupBox { Text = "フィルター — すべての条件を満たす行（AND）", Dock = DockStyle.Top, Height = 210, Padding = new Padding(10) };
        filterGroup.Controls.Add(_filters); filterGroup.Controls.Add(filterLine);
        var removeFilter = new Button { Text = "選択条件を削除", Dock = DockStyle.Bottom, Height = 34 };
        removeFilter.Click += (_, _) => { if (_filters.SelectedIndex >= 0) Result.Filters.RemoveAt(_filters.SelectedIndex); RefreshRules(doc); };
        filterGroup.Controls.Add(removeFilter);
        var sortGroup = new GroupBox { Text = "ソート — 上のキーから優先。列の型を使って比較", Dock = DockStyle.Fill, Padding = new Padding(10) };
        var sortLine = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40 };
        foreach (var descending in new[] { false, true })
        {
            var button = new Button { Text = descending ? "選択列を降順で追加" : "選択列を昇順で追加", AutoSize = true };
            button.Click += (_, _) => { if (_column.SelectedIndex < 0) return; Result.Sorts.Add(new SortRule(_column.SelectedIndex, descending)); RefreshRules(doc); };
            sortLine.Controls.Add(button);
        }
        var removeSort = new Button { Text = "選択キーを削除", AutoSize = true };
        removeSort.Click += (_, _) => { if (_sorts.SelectedIndex >= 0) Result.Sorts.RemoveAt(_sorts.SelectedIndex); RefreshRules(doc); };
        sortLine.Controls.Add(removeSort); sortGroup.Controls.Add(_sorts); sortGroup.Controls.Add(sortLine);
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
        body.Controls.Add(sortGroup); body.Controls.Add(filterGroup); Controls.Add(body);
        Dialogs.Buttons(this); RefreshRules(doc);
    }

    private void RefreshRules(CsvDocument doc)
    {
        _filters.Items.Clear(); foreach (var r in Result.Filters) _filters.Items.Add($"{doc.Columns[r.Column].HeaderName}  {_operator.Items[(int)r.Operator]}  {r.Value}");
        _sorts.Items.Clear(); foreach (var r in Result.Sorts) _sorts.Items.Add($"{doc.Columns[r.Column].HeaderName}  {(r.Descending ? "降順" : "昇順")}");
    }
}
