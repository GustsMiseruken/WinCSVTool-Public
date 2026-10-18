using WinCSVTool.Core;

namespace WinCSVTool;

internal sealed class DecodeForm : Form
{
    private readonly ComboBox _kind = new() { Width = 280, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _encoding = new() { Width = 280, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _plus = new() { Text = "URLの + を空白として扱う", Checked = true, AutoSize = true };
    private readonly CheckBox _whole = new() { Text = "選択列の全行に適用（現在のフィルターを使用）", AutoSize = true };
    private readonly TextBox _preview = new() { Width = 650, Height = 160, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, MaxLength = 0 };
    public DecodeKind Kind => (DecodeKind)_kind.SelectedIndex;
    public string EncodingName => (string)_encoding.SelectedItem!;
    public bool PlusAsSpace => _plus.Checked;
    public bool WholeView => _whole.Checked;

    public DecodeForm(string currentValue)
    {
        Text = "URL / Base64 デコード"; ClientSize = new Size(720, 620); StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Yu Gothic UI", 9.5f); BackColor = Color.White; MinimizeBox = false; MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog; AutoScaleMode = AutoScaleMode.Dpi;
        _kind.Items.AddRange(["URL デコード (%HH)", "Base64 デコード"]); _kind.SelectedIndex = 0;
        _encoding.Items.AddRange(["utf-8", "shift_jis", "euc-jp", "utf-16", "utf-16BE"]); _encoding.SelectedIndex = 0;
        Controls.Add(Dialogs.Body(new Label { Text = "デコード方式", AutoSize = true }, _kind,
            new Label { Text = "デコード後のバイト列の文字コード", AutoSize = true }, _encoding, _plus, _whole,
            new Label { Text = "現在のセルの変換プレビュー（適用前）", AutoSize = true }, _preview,
            new Label { Text = "通常は選択範囲に適用します。空欄は無視します。\r\n不正な値がある場合は、変更をすべて取り消します。", AutoSize = true }));
        Dialogs.Buttons(this);
        _preview.BackColor = UiTheme.Header;
        void Preview()
        {
            _plus.Enabled = Kind == DecodeKind.Url;
            try { _preview.Text = DecodeService.DecodeText(currentValue, Kind, EncodingName, PlusAsSpace); }
            catch (Exception ex) { _preview.Text = ex.Message; }
        }
        _kind.SelectedIndexChanged += (_, _) => Preview(); _encoding.SelectedIndexChanged += (_, _) => Preview(); _plus.CheckedChanged += (_, _) => Preview(); Preview();
    }
}
