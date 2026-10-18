using WinCSVTool.Core;

namespace WinCSVTool;

/// <summary>Provenance of the bundled reference files. URLs are displayed without opening a connection.</summary>
internal sealed class IpReferenceInfoForm : Form
{
    private readonly TextBox _details = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = true,
        ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, MaxLength = 0
    };

    internal IpReferenceInfoForm(IpDataMetadata metadata)
    {
        Text = "IP・ASN参照データの詳細"; ClientSize = new Size(790, 650); MinimumSize = new Size(590, 460);
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false; MinimizeBox = false; Padding = new Padding(22);
        AutoScaleMode = AutoScaleMode.Dpi;
        var heading = new Label { Dock = DockStyle.Top, Height = 62, Text = "オフライン参照データ\nファイルの基準日・出典・利用条件を確認できます。" };
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 58, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 14, 0, 0) };
        var close = new Button { Text = "閉じる", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(108, 36) };
        var copy = new Button { Text = "詳細をコピー", AutoSize = true, MinimumSize = new Size(138, 36) };
        copy.Click += (_, _) => Clipboard.SetText(_details.Text);
        footer.Controls.AddRange([close, copy]);
        _details.Text = Describe(metadata);
        Controls.Add(_details); Controls.Add(heading); Controls.Add(footer);
        AcceptButton = close; CancelButton = close;
        UiTheme.ThemeForm(this); _details.BorderStyle = BorderStyle.None;
        Shown += (_, _) => { _details.Select(0, 0); _details.ScrollToCaret(); close.Focus(); };
    }

    internal static string Describe(IpDataMetadata data) => string.Join("\r\n", new[]
    {
        $"データ基準日: {data.SnapshotDate}",
        $"提供元・帰属表示: {data.Attribution}",
        $"ライセンス: {data.License}", "",
        "出典", data.Source, "",
        "国・地域データ", $"取得元: {data.CountrySource}", $"ファイル: {data.CountryFile}",
        $"作成時刻 (UTC): {data.CountryBuildUtc}", $"SHA-256: {data.CountrySha256}", "",
        "ASN・事業者データ", $"取得元: {data.AsnSource}", $"ファイル: {data.AsnFile}",
        $"作成時刻 (UTC): {data.AsnBuildUtc}", $"SHA-256: {data.AsnSha256}", "",
        "ASN索引", $"ファイル: {data.AsnIndexFile}", $"登録組織数: {data.AsnOrganizations:N0}",
        $"SHA-256: {data.AsnIndexSha256}", "",
        "参照結果は国・地域や事業者を推定するための参考情報です。",
        "個人の国籍、IPアドレスの現在の所在地や所有者を証明するものではありません。",
        "特殊用途アドレスの区分はIANAの登録情報を基に判定します。"
    });
}
