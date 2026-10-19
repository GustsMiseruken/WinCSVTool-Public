using WinCSVTool.Core;

namespace WinCSVTool;

internal sealed class LogImportForm : Form
{
    private readonly ComboBox formats=new(){DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill};
    private readonly ComboBox encoding=new(){DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill};
    private readonly ComboBox escaping=new(){DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill};
    private readonly TextBox pattern=new(){Multiline=true,AcceptsReturn=true,ScrollBars=ScrollBars.Both,WordWrap=false,Dock=DockStyle.Fill};
    private readonly Label hint=new(){Dock=DockStyle.Fill,AutoSize=true};
    private readonly LogParserDefinition[] definitions=LogParserRegistry.Definitions.ToArray();
    private readonly ToolTip help=new(){AutoPopDelay=20000};
    internal LogImportOptions Options=>new(definitions[formats.SelectedIndex].Id,pattern.Text,(string)encoding.SelectedItem!,Enum.Parse<LogEscapeMode>((string)escaping.SelectedItem!));
    internal LogImportForm(string path,string defaultEncoding="auto")
    {
        Text="アクセスログの読み込み";StartPosition=FormStartPosition.CenterParent;ClientSize=new(760,480);MinimumSize=new(620,500);Font=new Font("Yu Gothic UI",9.5f);DoubleBuffered=true;
        var layout=new FeatureTable{Dock=DockStyle.Fill,ColumnCount=2,RowCount=7,Padding=new Padding(20)};
        layout.ColumnStyles.Add(new(SizeType.Absolute,150));layout.ColumnStyles.Add(new(SizeType.Percent,100));
        for(var i=0;i<5;i++)layout.RowStyles.Add(new(SizeType.AutoSize));layout.RowStyles.Add(new(SizeType.Percent,100));layout.RowStyles.Add(new(SizeType.AutoSize));
        var file=new Label{Text=Path.GetFileName(path),AutoSize=true,Dock=DockStyle.Fill,Margin=new Padding(0,0,0,12)};layout.Controls.Add(file,0,0);layout.SetColumnSpan(file,2);
        formats.Items.AddRange(definitions.Select(d=>(object)d.Name).ToArray());encoding.Items.AddRange(["auto","utf-8","utf-16le","utf-16be","cp932","euc-jp"]);encoding.SelectedItem=encoding.Items.Contains(defaultEncoding)?defaultEncoding:"auto";
        escaping.Items.AddRange(Enum.GetNames<LogEscapeMode>());escaping.SelectedIndex=0;
        encoding.FormattingEnabled=true;encoding.Format+=(_,e)=>e.Value=e.ListItem?.ToString() switch{"auto"=>"自動判定","cp932"=>"Shift_JIS（Windows-31J / CP932）","utf-16le"=>"UTF-16 LE","utf-16be"=>"UTF-16 BE",var value=>value?.ToUpperInvariant()};
        escaping.FormattingEnabled=true;escaping.Format+=(_,e)=>e.Value=e.ListItem?.ToString() switch{"Default"=>"標準のエスケープ（Default）","Json"=>"JSON形式のエスケープ（Json）","None"=>"変換しない（None）",var value=>value};
        encoding.DropDownWidth=escaping.DropDownWidth=360;
        help.SetToolTip(encoding,"ファイルの文字コードを選びます。nginxで日本語がASCIIのエスケープになっている場合は、元の文字コードを明示指定します。");
        help.SetToolTip(escaping,"ログ出力側の設定に合わせます。nginxのescape=default / json / noneに対応します。");
        pattern.PlaceholderText="使用中のログ出力設定から、変数・引用符・区切りを含む書式本体を貼り付けます。";
        void Row(int row,string label,Control control){layout.Controls.Add(new Label{Text=label,AutoSize=true,Anchor=AnchorStyles.Left,Margin=new Padding(0,8,10,8)},0,row);control.Margin=new Padding(0,4,0,4);layout.Controls.Add(control,1,row);}
        Row(1,"ログ形式",formats);Row(2,"文字コード",encoding);Row(3,"文字の表現",escaping);layout.Controls.Add(hint,0,4);layout.SetColumnSpan(hint,2);layout.Controls.Add(pattern,0,5);layout.SetColumnSpan(pattern,2);
        var footer=new FlowLayoutPanel{FlowDirection=FlowDirection.RightToLeft,Dock=DockStyle.Fill,AutoSize=true,Padding=new Padding(0,12,0,0)};
        var open=new Button{Text="読み込む",AutoSize=true,MinimumSize=new(110,36)};var cancel=new Button{Text="キャンセル",DialogResult=DialogResult.Cancel,AutoSize=true,MinimumSize=new(110,36)};
        open.Click+=(_,_)=>{try{_ = LogParserRegistry.Create(Options);DialogResult=DialogResult.OK;}catch(Exception error)when(error is ArgumentException or NotSupportedException){MessageBox.Show(this,error.Message,"ログ書式",MessageBoxButtons.OK,MessageBoxIcon.Information);}};
        footer.Controls.AddRange([cancel,open]);layout.Controls.Add(footer,0,6);layout.SetColumnSpan(footer,2);Controls.Add(layout);AcceptButton=open;CancelButton=cancel;
        formats.SelectedIndexChanged+=(_,_)=>{var selected=definitions[formats.SelectedIndex];pattern.Text=selected.DefaultPattern;escaping.Enabled=!selected.IsJson;hint.Text=selected.IsJson?"JSON列のパスをカンマで区切ります。例: timestamp,httpRequest.clientIp,$（$は原JSON）。":"書式本体を入力します（log_format / LogFormat 等の設定行全体は不要）。引用符・角括弧も元の書式どおり入力してください。";};
        var plain=path.EndsWith(".gz",StringComparison.OrdinalIgnoreCase)?Path.GetFileNameWithoutExtension(path):path;
        formats.SelectedIndex=Array.FindIndex(definitions,d=>d.Id==(plain.EndsWith(".jsonl",StringComparison.OrdinalIgnoreCase)||plain.EndsWith(".ndjson",StringComparison.OrdinalIgnoreCase)?"aws-waf":"nginx"));
        UiTheme.ThemeForm(this);
    }
    protected override void Dispose(bool disposing){if(disposing)help.Dispose();base.Dispose(disposing);}
}
