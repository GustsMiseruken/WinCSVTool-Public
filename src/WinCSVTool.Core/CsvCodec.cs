using System.Text;

namespace WinCSVTool.Core;

public sealed class CsvFormatException(string message, long recordNumber)
    : FormatException($"CSV レコード {recordNumber}: {message}")
{
    public long RecordNumber { get; } = recordNumber;
}

/// <summary>Streaming CSV codec. All cells remain strings, including leading zeros.</summary>
public static class CsvCodec
{
    public static IEnumerable<string[]> Read(TextReader reader, char delimiter = ',', CancellationToken token = default)
    {
        ValidateDelimiter(delimiter);
        var fields = new List<string>();
        var field = new StringBuilder();
        var state = 0; // 0 field start/unquoted, 1 quoted, 2 closing quote
        var fieldStarted = false;
        var recordStarted = false;
        long record = 1;
        var consumed = 0;
        var chars = new BufferedCharacters(reader);
        while (true)
        {
            if ((consumed++ & 4095) == 0) token.ThrowIfCancellationRequested();
            var next = chars.Read();
            if (next < 0)
            {
                if (state == 1) throw new CsvFormatException("引用符が閉じられていません。", record);
                if (recordStarted || fieldStarted || fields.Count > 0)
                {
                    fields.Add(field.ToString());
                    yield return fields.ToArray();
                }
                yield break;
            }
            var ch = (char)next;
            recordStarted = true;
            if (state == 1)
            {
                if (ch == '"') state = 2;
                else field.Append(ch);
                continue;
            }
            if (state == 2 && ch == '"') { field.Append('"'); state = 1; continue; }
            if (ch == delimiter)
            {
                fields.Add(field.ToString());
                field = field.Capacity > 65536 ? new StringBuilder() : field.Clear();
                state = 0;
                fieldStarted = false;
                continue;
            }
            if (ch is '\r' or '\n')
            {
                if (ch == '\r' && chars.Peek() == '\n') chars.Read();
                fields.Add(field.ToString());
                yield return fields.ToArray();
                fields.Clear();
                field = field.Capacity > 65536 ? new StringBuilder() : field.Clear();
                state = 0;
                fieldStarted = false;
                recordStarted = false;
                record++;
                continue;
            }
            if (state == 2) throw new CsvFormatException("閉じ引用符の後には区切り文字または改行が必要です。", record);
            if (ch == '"')
            {
                if (fieldStarted) throw new CsvFormatException("引用符はセルの先頭に置いてください。", record);
                state = 1;
                fieldStarted = true;
                continue;
            }
            field.Append(ch);
            fieldStarted = true;
        }
    }

    public static void WriteRecord(TextWriter writer, IEnumerable<string> values, char delimiter = ',', bool escapeSpreadsheetFormulas = false)
    {
        ValidateDelimiter(delimiter);
        var first = true;
        foreach (var original in values)
        {
            var value = escapeSpreadsheetFormulas ? EscapeSpreadsheetFormula(original) : original;
            if (!first) writer.Write(delimiter);
            first = false;
            if (value.IndexOfAny([delimiter, '"', '\r', '\n']) < 0) writer.Write(value);
            else
            {
                writer.Write('"');
                foreach (var ch in value)
                {
                    if (ch == '"') writer.Write('"');
                    writer.Write(ch);
                }
                writer.Write('"');
            }
        }
        writer.Write("\r\n");
    }

    /// <summary>Opt-in output transformation. Prefixing an apostrophe changes the exported value; CSV quoting alone does not prevent formula interpretation.</summary>
    public static string EscapeSpreadsheetFormula(string value)
    {
        var offset = 0;
        while (offset < value.Length && (char.IsWhiteSpace(value[offset]) || char.IsControl(value[offset]) || value[offset] == '\uFEFF')) offset++;
        // Tabs/newlines can also change how spreadsheet importers interpret the start of a cell.
        var leadingControl = value.Length > 0 && value[0] is '\t' or '\r' or '\n';
        return leadingControl || (offset < value.Length && value[offset] is '=' or '+' or '-' or '@') ? "'" + value : value;
    }

    public static void ValidateDelimiter(char delimiter)
    {
        if (delimiter is '"' or '\r' or '\n' or '\0')
            throw new ArgumentException("区切り文字に引用符・改行・NULは指定できません。", nameof(delimiter));
    }

    private sealed class BufferedCharacters(TextReader reader)
    {
        private readonly char[] buffer = new char[32 * 1024];
        private int offset;
        private int count;
        public int Peek()
        {
            if (offset >= count) { count = reader.Read(buffer, 0, buffer.Length); offset = 0; }
            return count == 0 ? -1 : buffer[offset];
        }
        public int Read() { var value = Peek(); if (value >= 0) offset++; return value; }
    }
}
