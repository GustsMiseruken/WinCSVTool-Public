using System.Text.Json;

namespace WinCSVTool.Core;

internal sealed class JsonLogParser : ILogRecordParser
{
    private readonly string[] paths;
    public IReadOnlyList<string> Headers { get; }
    internal JsonLogParser(string fields)
    {
        if(fields.Length>32768)throw new ArgumentException("JSON列設定が長すぎます。");
        paths=fields.Split(',',StringSplitOptions.TrimEntries);
        if(paths.Any(p=>p.Length==0||p.Length>512||p!="$"&&p.Split('.').Any(string.IsNullOrEmpty)))throw new ArgumentException("JSON列は timestamp,httpRequest.clientIp,$ のように指定してください。");
        Headers=paths.Select(p=>p=="$"?"raw_json":p).ToArray();
    }
    public string[] Parse(string line,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();
        using var document=JsonDocument.Parse(line,new JsonDocumentOptions{MaxDepth=64});
        if(document.RootElement.ValueKind!=JsonValueKind.Object)throw new FormatException("JSONLの各行はJSONオブジェクトである必要があります。");
        var values=new string[paths.Length];
        for(var i=0;i<paths.Length;i++)
        {
            token.ThrowIfCancellationRequested();if(paths[i]=="$"){values[i]=line;continue;}
            var value=document.RootElement;var found=true;
            foreach(var name in paths[i].Split('.'))if(value.ValueKind!=JsonValueKind.Object||!value.TryGetProperty(name,out value)){found=false;break;}
            values[i]=!found||value.ValueKind==JsonValueKind.Null?"":value.ValueKind==JsonValueKind.String?value.GetString()!:value.GetRawText();
        }
        return values;
    }
}
