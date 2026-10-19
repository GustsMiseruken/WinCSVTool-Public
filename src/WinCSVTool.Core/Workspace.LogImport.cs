using System.Text;
using Microsoft.Data.Sqlite;

namespace WinCSVTool.Core;

public sealed partial class Workspace
{
    public CsvDocument ImportLog(string path,LogImportOptions options,CancellationToken token=default,IProgress<ImportProgress>? progress=null)
    {
        token.ThrowIfCancellationRequested();var parser=LogParserRegistry.Create(options);path=Path.GetFullPath(path);
        lock(gate)
        {
            EnsureDocumentCapacity(1);
            using var input=CompressedText.OpenRead(path,options.EncodingName,token);
            parser=LogParserRegistry.Create(options with {EncodingName=input.EncodingName});
            var document=NewDocument(Path.GetFileName(path),parser.Headers);
            document.ImportSourcePath=path;document.SourcePath=null;document.EncodingName=input.EncodingName;document.IsDirty=true;
            using var connection=OpenConnection();using var transaction=connection.BeginTransaction();using var cancellation=ObserveCancellation(connection,token);
            CreateTable(connection,transaction,document);
            using var insert=connection.CreateCommand();insert.Transaction=transaction;
            insert.CommandText=$"INSERT INTO {Quote(document.TableName)} ({ColumnNames(document)}) VALUES ({string.Join(',',document.Columns.Select((_,i)=>"$v"+i))})";
            for(var i=0;i<document.Columns.Count;i++)insert.Parameters.Add("$v"+i,SqliteType.Text);insert.Prepare();
            progress?.Report(new(0,0,input.TotalBytes));
            long lineNumber=0;
            foreach(var line in LogLines(input.Reader,token))
            {
                token.ThrowIfCancellationRequested();lineNumber++;
                string[] values;
                try{values=parser.Parse(line,token);}
                catch(Exception error)when(error is FormatException or System.Text.Json.JsonException or DecoderFallbackException or System.Text.RegularExpressions.RegexMatchTimeoutException)
                {throw new FormatException($"ログの {lineNumber:N0} 行目: {error.Message}",error);}
                if(values.Length!=document.Columns.Count)throw new FormatException($"ログの {lineNumber:N0} 行目: パーサーの列数が一致しません。");
                for(var i=0;i<values.Length;i++)insert.Parameters[i].Value=values[i];ExecuteCommand(insert,token);document.RowCount++;
                if((document.RowCount&8191)==0)progress?.Report(new(document.RowCount,input.BytesRead,input.TotalBytes));
            }
            var map=BuildMap(connection,transaction,document,new(),token);
            WriteRecoveryCheckpoint(connection,transaction,proposedDocuments:documents.Append(document).ToArray());
            progress?.Report(new(document.RowCount,input.TotalBytes,input.TotalBytes));token.ThrowIfCancellationRequested();transaction.Commit();
            documents.Add(document);maps[document.TableName]=[map];return document;
        }
    }
    private static IEnumerable<string> LogLines(TextReader reader,CancellationToken token)
    {
        const int maximumLine=16*1024*1024;var buffer=new char[65536];var line=new StringBuilder();var previousCr=false;long number=1;
        int count;while((count=reader.Read(buffer,0,buffer.Length))>0)
        {
            for(var i=0;i<count;i++)
            {
                if((i&4095)==0)token.ThrowIfCancellationRequested();var ch=buffer[i];
                if(ch=='\n'&&previousCr){previousCr=false;continue;}
                if(ch is '\r' or '\n'){yield return line.ToString();line.Clear();number++;previousCr=ch=='\r';continue;}
                previousCr=false;if(line.Length>=maximumLine)throw new FormatException($"ログの {number:N0} 行目が16Mi文字を超えています。");line.Append(ch);
            }
        }
        token.ThrowIfCancellationRequested();if(line.Length>0)yield return line.ToString();
    }
    private static void ValidateLogDestination(CsvDocument document,string path)
    {
        if(document.ImportSourcePath is { } source&&string.Equals(Path.GetFullPath(path),Path.GetFullPath(source),StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("アクセスログの原本には書き込めません。別名のCSVまたはCSV.gzへ保存してください。");
    }
    internal void ValidateExportDestination(string path){lock(gate)foreach(var document in documents)ValidateLogDestination(document,path);}
}
