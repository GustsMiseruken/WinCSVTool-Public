using WinCSVTool.Core;

namespace WinCSVTool;

internal sealed partial class MainForm
{
    internal async Task ImportAccessLogAsync()
    {
        if(_busy)return;
        using var open=new OpenFileDialog{Filter="アクセスログ / gzip|*.log;*.log.gz;*.jsonl;*.jsonl.gz;*.ndjson;*.ndjson.gz;*.gz;*.txt|すべてのファイル|*.*",Multiselect=true};
        if(open.ShowDialog(this)!=DialogResult.OK)return;
        foreach(var path in open.FileNames)if(!await OpenAccessLogAsync(path))break;
    }
    private async Task<bool> OpenAccessLogAsync(string path)
    {
        using var settings=new LogImportForm(path,Settings.DefaultEncodingName);
        if(settings.ShowDialog(this)!=DialogResult.OK)return false;
        var options=settings.Options;CsvDocument? imported=null;
        var progress=new Progress<ImportProgress>(p=>_state.Text=$"{Path.GetFileName(path)} — {p.Rows:N0} 行を読み込み");
        if(!await RunWork("アクセスログを読み込み",token=>imported=Workspace.ImportLog(path,options,token,progress)))return false;
        await AddDocumentAsync(imported!);return true;
    }
    private static bool IsAccessLog(string path)
    {
        var name=path.EndsWith(".gz",StringComparison.OrdinalIgnoreCase)?Path.GetFileNameWithoutExtension(path):path;
        return new[]{".log",".jsonl",".ndjson"}.Any(extension=>name.EndsWith(extension,StringComparison.OrdinalIgnoreCase))||Path.GetFileName(name).Equals("access_log",StringComparison.OrdinalIgnoreCase);
    }
}
