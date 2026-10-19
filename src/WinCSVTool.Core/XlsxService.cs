using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Data.Sqlite;

namespace WinCSVTool.Core;

/// <summary>Streams XLSX cell values without Excel, formula evaluation, or a workbook-sized memory allocation.</summary>
public sealed class XlsxService(Workspace workspace)
{
    public const int MaximumRows = 1_048_576;
    public const int MaximumColumns = 16_384;
    private const int MaximumCellLength = 32_767;
    private const int MaximumMetadataAttributeLength = 4096;
    private const int MaximumMetadataAttributes = 64;
    private const int MaximumMetadataDepth = 64;
    private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly Regex EscapePattern = new("_x[0-9a-fA-F]{4}_", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public IReadOnlyList<CsvDocument> Import(string path, bool hasHeader = true, CancellationToken token = default, IProgress<ImportProgress>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        path = Path.GetFullPath(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        if (archive.Entries.Count > 100_000) throw new InvalidDataException("XLSX 内の部品数が上限を超えています。");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
            if (!entries.TryAdd(entry.FullName, entry)) throw new InvalidDataException("XLSX 内に同じ名前の部品が重複しています。");
        ZipArchiveEntry GetEntry(string name) => entries.TryGetValue(name, out var entry) ? entry : throw new InvalidDataException($"XLSX の必須部品がありません: {name}");
        var packageRelationships = ReadRelationships(GetEntry("_rels/.rels"), "", token);
        var workbookPath = packageRelationships.Values.FirstOrDefault(r => r.Type.EndsWith("/officeDocument", StringComparison.Ordinal) && !r.External)?.Path
            ?? throw new InvalidDataException("XLSX のブック情報がありません。");
        var relationships = ReadRelationships(GetEntry(RelationshipsPath(workbookPath)), workbookPath, token);
        var sheets = new List<(string Name, string Path)>();
        var date1904 = false;
        using (var reader = OpenMetadata(GetEntry(workbookPath), 4 * 1024 * 1024, token))
        {
            RequireRoot(reader, "workbook");
            var elements = 0;
            CheckMetadataElement(reader, ref elements, 65_536);
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (reader.NodeType != XmlNodeType.Element) continue;
                CheckMetadataElement(reader, ref elements, 65_536);
                if (reader.LocalName == "workbookPr") date1904 = reader.GetAttribute("date1904") is "1" or "true";
                if (reader.LocalName != "sheet") continue;
                var id = reader.GetAttribute("id", RelNs) ?? reader.GetAttribute("id", "http://purl.oclc.org/ooxml/officeDocument/relationships");
                if (id is null || !relationships.TryGetValue(id, out var relationship) || relationship.External || !relationship.Type.EndsWith("/worksheet", StringComparison.Ordinal))
                    throw new InvalidDataException("ワークシートの参照が不正です。外部シートやグラフシートは読み込めません。");
                var name = DecodeText(reader.GetAttribute("name") ?? $"Sheet{sheets.Count + 1}");
                if (name.Length > 31) throw new InvalidDataException("XLSX のシート名が上限（31 文字）を超えています。");
                sheets.Add((name, relationship.Path));
                if (sheets.Count > 1024) throw new InvalidDataException("一度に読み込める XLSX は 1,024 シートまでです。");
            }
        }
        if (sheets.Count == 0) throw new InvalidDataException("XLSX にワークシートがありません。");
        var stylesPath = relationships.Values.FirstOrDefault(r => !r.External && r.Type.EndsWith("/styles", StringComparison.Ordinal))?.Path;
        var styles = stylesPath is null ? [] : ReadStyles(GetEntry(stylesPath), token);
        var sharedPath = relationships.Values.FirstOrDefault(r => !r.External && r.Type.EndsWith("/sharedStrings", StringComparison.Ordinal))?.Path;
        SharedStrings? strings = null;
        long importedRows = 0;
        try
        {
            progress?.Report(new ImportProgress(0, 0, file.Length));
            var sources = sheets.Select(sheet => new XlsxSheetSource(sheet.Name, () => ReadRows(GetEntry(sheet.Path), strings!, styles, date1904, token, () =>
            {
                importedRows++;
                if ((importedRows & 8191) == 0) progress?.Report(new ImportProgress(importedRows, Math.Min(file.Position, file.Length), file.Length));
            }))).ToArray();
            var documents = workspace.ImportXlsx(path, sources, hasHeader, (connection, transaction) =>
            {
                strings = new SharedStrings(connection, transaction, token);
                if (sharedPath is not null) strings.Load(GetEntry(sharedPath));
            }, (_, _) => strings!.Drop(), token);
            progress?.Report(new ImportProgress(documents.Sum(d => d.RowCount), file.Length, file.Length));
            return documents;
        }
        catch (SqliteException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        finally { strings?.Dispose(); }
    }

    public void Export(string path, IReadOnlyList<CsvDocument> documents, bool includeHeader = true, CancellationToken token = default, RowScope? scope = null)
    {
        token.ThrowIfCancellationRequested();
        workspace.ValidateExportDestination(path);
        if (documents.Count == 0) throw new ArgumentException("保存するシートを選択してください。", nameof(documents));
        if (documents.Count > 1024) throw new ArgumentException("一度に保存できるシートは 1,024 枚までです。", nameof(documents));
        if (scope is not null)
        {
            if (documents.Count != 1) throw new ArgumentException("行スコープは 1 シートの保存に使用してください。", nameof(documents));
            workspace.ValidateScope(documents[0], scope);
        }
        foreach (var document in documents)
        {
            if (!workspace.Documents.Contains(document)) throw new InvalidOperationException("この作業領域のシートではありません。");
            if (document.Columns.Count > MaximumColumns || (scope?.Count ?? document.RowCount) > MaximumRows - (includeHeader ? 1 : 0))
                throw new InvalidDataException($"シート「{document.DisplayName}」が XLSX の上限（{MaximumRows:N0} 行、{MaximumColumns:N0} 列）を超えています。CSV 形式で保存してください。");
        }
        path = Path.GetFullPath(path);
        var names = MakeSheetNames(documents);
        using var output = new AtomicFile(path);
        using (var archive = new ZipArchive(output.Stream, ZipArchiveMode.Create, true))
        {
            WriteXml(archive, "[Content_Types].xml", xml =>
            {
                const string ns = "http://schemas.openxmlformats.org/package/2006/content-types";
                xml.WriteStartElement("Types", ns);
                xml.WriteStartElement("Default", ns); xml.WriteAttributeString("Extension", "rels"); xml.WriteAttributeString("ContentType", "application/vnd.openxmlformats-package.relationships+xml"); xml.WriteEndElement();
                xml.WriteStartElement("Default", ns); xml.WriteAttributeString("Extension", "xml"); xml.WriteAttributeString("ContentType", "application/xml"); xml.WriteEndElement();
                WriteContentType(xml, ns, "/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
                for (var i = 0; i < documents.Count; i++) WriteContentType(xml, ns, $"/xl/worksheets/sheet{i + 1}.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
                xml.WriteEndElement();
            });
            WriteXml(archive, "_rels/.rels", xml =>
            {
                xml.WriteStartElement("Relationships", PackageRelNs);
                WriteRelationship(xml, "rId1", "officeDocument", "xl/workbook.xml");
                xml.WriteEndElement();
            });
            WriteXml(archive, "xl/workbook.xml", xml =>
            {
                xml.WriteStartElement("workbook", MainNs);
                xml.WriteAttributeString("xmlns", "r", null, RelNs);
                xml.WriteStartElement("sheets", MainNs);
                for (var i = 0; i < documents.Count; i++)
                {
                    xml.WriteStartElement("sheet", MainNs); xml.WriteAttributeString("name", EncodeText(names[i]));
                    xml.WriteAttributeString("sheetId", (i + 1).ToString(CultureInfo.InvariantCulture)); xml.WriteAttributeString("r", "id", RelNs, $"rId{i + 1}"); xml.WriteEndElement();
                }
                xml.WriteEndElement(); xml.WriteEndElement();
            });
            WriteXml(archive, "xl/_rels/workbook.xml.rels", xml =>
            {
                xml.WriteStartElement("Relationships", PackageRelNs);
                for (var i = 0; i < documents.Count; i++) WriteRelationship(xml, $"rId{i + 1}", "worksheet", $"worksheets/sheet{i + 1}.xml");
                xml.WriteEndElement();
            });
            for (var i = 0; i < documents.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var document = documents[i];
                WriteXml(archive, $"xl/worksheets/sheet{i + 1}.xml", xml =>
                {
                    xml.WriteStartElement("worksheet", MainNs);
                    xml.WriteStartElement("sheetData", MainNs);
                    var number = 0;
                    void WriteRow(IReadOnlyList<string> values)
                    {
                        token.ThrowIfCancellationRequested();
                        if (++number > MaximumRows) throw new InvalidDataException("XLSX の行数上限を超えています。");
                        xml.WriteStartElement("row", MainNs); xml.WriteAttributeString("r", number.ToString(CultureInfo.InvariantCulture));
                        for (var column = 0; column < values.Count; column++)
                        {
                            var value = values[column];
                            if (value.Length > MaximumCellLength) throw new InvalidDataException($"シート「{names[i]}」の {number} 行、{column + 1} 列の文字数が XLSX の上限（32,767 文字）を超えています。");
                            xml.WriteStartElement("c", MainNs); xml.WriteAttributeString("r", CellReference(column, number)); xml.WriteAttributeString("t", "inlineStr");
                            xml.WriteStartElement("is", MainNs); xml.WriteStartElement("t", MainNs); xml.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve");
                            xml.WriteString(EncodeText(value)); xml.WriteEndElement(); xml.WriteEndElement(); xml.WriteEndElement();
                        }
                        xml.WriteEndElement();
                    }
                    if (includeHeader) WriteRow(document.Columns.Select(c => c.HeaderName).ToArray());
                    var rows = scope is null ? workspace.EnumerateRows(document, token: token) : workspace.EnumerateRows(document, scope, token);
                    foreach (var row in rows) WriteRow(row.Values);
                    xml.WriteEndElement(); xml.WriteEndElement();
                });
            }
        }
        output.Commit(token);
        if (scope is not null) return;
        var workbookId = Guid.NewGuid().ToString("N");
        for (var i = 0; i < documents.Count; i++)
        {
            var document = documents[i];
            document.WorkbookId = workbookId; document.WorksheetName = names[i]; document.WorkbookSourcePath = path;
            document.SourcePath = path; document.DisplayName = names[i]; document.HasHeader = includeHeader; document.IsDirty = false;
        }
    }

    private static IEnumerable<XlsxRow> ReadRows(ZipArchiveEntry entry, SharedStrings strings, IReadOnlyList<CellStyle> styles, bool date1904, CancellationToken token, Action rowRead)
    {
        using var reader = OpenXml(entry);
        RequireRoot(reader, "worksheet");
        long previous = 0;
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "row") continue;
            var number = reader.GetAttribute("r") is { } attribute ? ParsePositive(attribute, "行番号") : previous + 1;
            if (number <= previous || number > MaximumRows) throw new InvalidDataException("XLSX の行番号が不正です。");
            var values = new List<string>();
            using (var row = reader.ReadSubtree())
            {
                var lastColumn = -1;
                while (row.Read())
                {
                    token.ThrowIfCancellationRequested();
                    if (row.NodeType != XmlNodeType.Element || row.LocalName != "c") continue;
                    var reference = row.GetAttribute("r");
                    var column = reference is null ? lastColumn + 1 : ParseCellReference(reference, number);
                    if (column <= lastColumn || column >= MaximumColumns) throw new InvalidDataException("XLSX のセル順序または列番号が不正です。");
                    lastColumn = column;
                    var kind = row.GetAttribute("t");
                    var style = row.GetAttribute("s") is { } styleAttribute ? ParseNonnegative(styleAttribute, "スタイル番号") : 0;
                    var raw = "";
                    var inline = "";
                    using (var cell = row.ReadSubtree())
                    {
                        while (cell.Read())
                        {
                            if (cell.NodeType != XmlNodeType.Element) continue;
                            if (cell.LocalName == "v") raw = ReadElementText(cell);
                            else if (cell.LocalName == "is") inline = ReadRichText(cell);
                        }
                    }
                    var value = kind switch
                    {
                        "s" => strings.Get(ParseNonnegative(raw, "共有文字列番号")),
                        "inlineStr" => inline,
                        "b" => raw == "1" ? "TRUE" : raw == "0" ? "FALSE" : throw new InvalidDataException("XLSX の真偽値が不正です。"),
                        "str" or "e" or "d" => DecodeText(raw),
                        _ => FormatNumber(raw, style < styles.Count ? styles[(int)style] : new CellStyle(StyleKind.Number), date1904)
                    };
                    if (value.Length > MaximumCellLength) throw new InvalidDataException("XLSX のセル文字数が上限（32,767 文字）を超えています。");
                    while (values.Count <= column) values.Add("");
                    values[column] = value;
                }
            }
            for (var missing = previous + 1; missing < number; missing++) { token.ThrowIfCancellationRequested(); rowRead(); yield return new XlsxRow(missing, []); }
            previous = number;
            rowRead();
            yield return new XlsxRow(number, values.ToArray());
        }
    }

    private sealed record Relationship(string Type, string Path, bool External);
    private static Dictionary<string, Relationship> ReadRelationships(ZipArchiveEntry entry, string ownerPart, CancellationToken token)
    {
        var relationships = new Dictionary<string, Relationship>(StringComparer.Ordinal);
        using var reader = OpenMetadata(entry, 8 * 1024 * 1024, token);
        reader.MoveToContent();
        if (reader.LocalName != "Relationships") throw new InvalidDataException("XLSX の関連情報が不正です。");
        var elements = 0;
        CheckMetadataElement(reader, ref elements, 32_768);
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element) continue;
            CheckMetadataElement(reader, ref elements, 32_768);
            if (reader.LocalName != "Relationship") continue;
            var id = reader.GetAttribute("Id") ?? throw new InvalidDataException("XLSX の関連 ID がありません。");
            var type = reader.GetAttribute("Type") ?? "";
            var target = reader.GetAttribute("Target") ?? throw new InvalidDataException("XLSX の参照先がありません。");
            var external = reader.GetAttribute("TargetMode") == "External";
            var part = external ? "" : ResolvePart(ownerPart, target);
            if (!relationships.TryAdd(id, new Relationship(type, part, external))) throw new InvalidDataException("XLSX の関連 ID が重複しています。");
        }
        return relationships;
    }

    private static string ResolvePart(string ownerPart, string target)
    {
        if (target.Contains('\\') || target.Contains(':') || target.Contains('?') || target.Contains('#')) throw new InvalidDataException("XLSX の部品参照が不正です。");
        target = Uri.UnescapeDataString(target);
        if (target.Contains('\\') || target.Contains(':') || target.Contains('\0')) throw new InvalidDataException("XLSX の部品参照が不正です。");
        var basePath = target.StartsWith('/') ? "" : ownerPart[..(ownerPart.LastIndexOf('/') + 1)];
        var parts = new List<string>();
        foreach (var part in (basePath + target.TrimStart('/')).Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..") { if (parts.Count == 0) throw new InvalidDataException("XLSX の部品参照がルート外を指しています。"); parts.RemoveAt(parts.Count - 1); }
            else parts.Add(part);
        }
        return string.Join('/', parts);
    }
    private static string RelationshipsPath(string part)
    {
        var split = part.LastIndexOf('/');
        return part[..(split + 1)] + "_rels/" + part[(split + 1)..] + ".rels";
    }

    private static XmlReader OpenXml(ZipArchiveEntry entry) => XmlReader.Create(entry.Open(), XmlSettings(128L * 1024 * 1024 * 1024));
    private static XmlReaderSettings XmlSettings(long maximumCharacters) => new()
    {
        DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, CloseInput = true, IgnoreComments = true, IgnoreProcessingInstructions = true,
        MaxCharactersInDocument = maximumCharacters, MaxCharactersFromEntities = 0
    };
    private static XmlReader OpenMetadata(ZipArchiveEntry entry, long maximumBytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Check the declared size before the XML parser can allocate a large attribute,
        // and enforce the actual decompressed size independently while it reads.
        if (entry.Length > maximumBytes) throw new InvalidDataException($"XLSX のメタデータ部品が大きすぎます: {entry.FullName}");
        var stream = new BoundedReadStream(entry.Open(), maximumBytes, token);
        try { return XmlReader.Create(stream, XmlSettings(maximumBytes)); }
        catch { stream.Dispose(); throw; }
    }
    private static void CheckMetadataElement(XmlReader reader, ref int elements, int maximumElements, int depthOffset = 0)
    {
        if (reader.NodeType != XmlNodeType.Element) return;
        if (++elements > maximumElements || reader.Depth + depthOffset > MaximumMetadataDepth)
            throw new InvalidDataException("XLSX のメタデータ要素数または入れ子の深さが上限を超えています。");
        if (reader.AttributeCount > MaximumMetadataAttributes)
            throw new InvalidDataException("XLSX のメタデータ属性数が上限を超えています。");
        if (!reader.MoveToFirstAttribute()) return;
        do
        {
            if (reader.Name.Length > MaximumMetadataAttributeLength || reader.Value.Length > MaximumMetadataAttributeLength)
                throw new InvalidDataException("XLSX のメタデータ属性が長すぎます。");
        } while (reader.MoveToNextAttribute());
        reader.MoveToElement();
    }
    private static void RequireRoot(XmlReader reader, string name)
    {
        reader.MoveToContent();
        if (reader.LocalName != name || reader.NamespaceURI is not (MainNs or "http://purl.oclc.org/ooxml/spreadsheetml/main")) throw new InvalidDataException($"XLSX の {name} 部品が不正です。");
    }
    private static string ReadElementText(XmlReader reader)
    {
        var text = new StringBuilder();
        using var element = reader.ReadSubtree();
        var buffer = ArrayPool<char>.Shared.Rent(4096);
        try
        {
            while (element.Read())
                if (element.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
                {
                    int read;
                    while ((read = element.ReadValueChunk(buffer, 0, buffer.Length)) > 0)
                    {
                        if (text.Length + read > MaximumCellLength * 7) throw new InvalidDataException("XLSX のセル文字数が上限を超えています。");
                        text.Append(buffer, 0, read);
                    }
                }
        }
        finally { ArrayPool<char>.Shared.Return(buffer); }
        return text.ToString();
    }
    private static string ReadRichText(XmlReader reader)
    {
        var text = new StringBuilder();
        using var element = reader.ReadSubtree();
        while (element.Read())
        {
            if (element.NodeType != XmlNodeType.Element) continue;
            if (element.LocalName == "rPh") { using var ignored = element.ReadSubtree(); while (ignored.Read()) { } }
            else if (element.LocalName == "t")
            {
                var run = DecodeText(ReadElementText(element));
                if (text.Length + run.Length > MaximumCellLength) throw new InvalidDataException("XLSX のセル文字数が上限を超えています。");
                text.Append(run);
            }
        }
        return text.ToString();
    }
    private static string DecodeText(string text) => EscapePattern.Replace(text, match => ((char)int.Parse(match.Value.AsSpan(2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString());
    private static string EncodeText(string text)
    {
        text = EscapePattern.Replace(text, match => "_x005F_" + match.Value[1..]);
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
            if (character < 32 && character is not ('\t' or '\n')) builder.Append("_x").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture)).Append('_');
            else builder.Append(character);
        return builder.ToString();
    }
    private static long ParsePositive(string value, string label)
    {
        var result = ParseNonnegative(value, label);
        return result > 0 ? result : throw new InvalidDataException($"XLSX の {label} が不正です。");
    }
    private static long ParseNonnegative(string value, string label) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) && result >= 0 ? result : throw new InvalidDataException($"XLSX の {label} が不正です。");
    private static int ParseCellReference(string reference, long row)
    {
        var column = 0;
        var index = 0;
        while (index < reference.Length && reference[index] is >= 'A' and <= 'Z')
        {
            if (column > MaximumColumns) throw new InvalidDataException("XLSX の列番号が上限を超えています。");
            column = column * 26 + reference[index++] - 'A' + 1;
        }
        if (column == 0 || ParsePositive(reference[index..], "セル参照") != row) throw new InvalidDataException("XLSX のセル参照が不正です。");
        return column - 1;
    }
    private static string CellReference(int column, int row)
    {
        var letters = "";
        for (column++; column > 0; column = (column - 1) / 26) letters = (char)('A' + (column - 1) % 26) + letters;
        return letters + row.ToString(CultureInfo.InvariantCulture);
    }

    private enum StyleKind { Number, Date, Time, DateTime, Duration, ZeroPadded }
    private sealed record CellStyle(StyleKind Kind, int Width = 0, FormatSection[]? Sections = null, bool Conditional = false);
    private sealed record FormatSection(CellStyle Style, FormatCondition? Condition);
    private sealed record FormatCondition(string Operator, double Operand)
    {
        public bool Matches(double value) => Operator switch
        {
            "<" => value < Operand, "<=" => value <= Operand, ">" => value > Operand,
            ">=" => value >= Operand, "=" => value == Operand, "<>" => value != Operand, _ => false
        };
    }
    private static List<CellStyle> ReadStyles(ZipArchiveEntry entry, CancellationToken token)
    {
        var formats = new Dictionary<long, string>();
        var styles = new List<CellStyle>();
        using var reader = OpenMetadata(entry, 16 * 1024 * 1024, token);
        RequireRoot(reader, "styleSheet");
        var elements = 0;
        CheckMetadataElement(reader, ref elements, 262_144);
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element) continue;
            CheckMetadataElement(reader, ref elements, 262_144);
            if (reader.LocalName == "numFmt")
            {
                var id = ParseNonnegative(reader.GetAttribute("numFmtId") ?? "", "数値書式番号");
                var format = reader.GetAttribute("formatCode") ?? "";
                if (format.Length > 4096) throw new InvalidDataException("XLSX の数値書式が長すぎます。");
                formats[id] = format;
                if (formats.Count > 65_536) throw new InvalidDataException("XLSX の書式数が上限を超えています。");
            }
            else if (reader.LocalName == "cellXfs")
            {
                var depth = reader.Depth;
                using var xfs = reader.ReadSubtree();
                while (xfs.Read())
                {
                    token.ThrowIfCancellationRequested();
                    if (xfs.NodeType != XmlNodeType.Element) continue;
                    if (xfs.Depth > 0) CheckMetadataElement(xfs, ref elements, 262_144, depth);
                    if (xfs.LocalName != "xf") continue;
                    var id = ParseNonnegative(xfs.GetAttribute("numFmtId") ?? "0", "数値書式番号");
                    styles.Add(ClassifyStyle(id, formats.GetValueOrDefault(id)));
                    if (styles.Count > 65_536) throw new InvalidDataException("XLSX の書式数が上限を超えています。");
                }
            }
        }
        return styles;
    }
    private static CellStyle ClassifyStyle(long id, string? format)
    {
        if (format is null) return ClassifySection(id, null);
        var sections = new List<FormatSection>(4);
        var quoted = false;
        var start = 0;
        FormatCondition? condition = null;
        var text = false;
        var textSection = -1;
        for (var i = 0; i <= format.Length; i++)
        {
            if (i == format.Length || (!quoted && format[i] == ';'))
            {
                if (quoted || sections.Count == 4) return new CellStyle(StyleKind.Number);
                sections.Add(new FormatSection(ClassifySection(id, format[start..i]), condition));
                if (text) textSection = sections.Count - 1;
                start = i + 1; condition = null; text = false;
                continue;
            }
            var character = format[i];
            if (character == '"') { quoted = !quoted; continue; }
            if (quoted) continue;
            if (character is '\\' or '_' or '*')
            {
                if (++i >= format.Length) return new CellStyle(StyleKind.Number);
                continue;
            }
            if (character == '@') { text = true; continue; }
            if (character != '[') continue;
            var close = format.IndexOf(']', i + 1);
            if (close < 0) return new CellStyle(StyleKind.Number);
            var bracket = format.AsSpan(i + 1, close - i - 1).Trim();
            if (!bracket.IsEmpty && bracket[0] is '<' or '>' or '=')
            {
                if (condition is not null || sections.Count >= 2) return new CellStyle(StyleKind.Number);
                var operatorLength = bracket.Length > 1 && (bracket[1] == '=' || bracket[0] == '<' && bracket[1] == '>') ? 2 : 1;
                var operation = bracket[..operatorLength].ToString();
                if (operation is not ("<" or "<=" or ">" or ">=" or "=" or "<>") ||
                    !double.TryParse(bracket[operatorLength..], NumberStyles.Float, CultureInfo.InvariantCulture, out var operand) || !double.IsFinite(operand))
                    return new CellStyle(StyleKind.Number);
                condition = new FormatCondition(operation, operand);
            }
            i = close;
        }
        if (textSection >= 0 && textSection != sections.Count - 1) return new CellStyle(StyleKind.Number);
        if (textSection == sections.Count - 1) sections.RemoveAt(textSection);
        else if (sections.Count == 4) sections.RemoveAt(3);
        if (sections.Count == 0) return new CellStyle(StyleKind.Number);
        var conditional = sections.Any(section => section.Condition is not null);
        // Ambiguous/unsupported condition layouts retain numeric text instead of guessing a date.
        if (conditional && sections[0].Condition is null) return new CellStyle(StyleKind.Number);
        if (sections.Count == 1 && !conditional) return sections[0].Style;
        return new CellStyle(StyleKind.Number, Sections: sections.ToArray(), Conditional: conditional);
    }
    private static CellStyle? SelectStyle(CellStyle style, double number)
    {
        if (style.Sections is not { } sections) return style;
        if (style.Conditional)
        {
            foreach (var section in sections)
                if (section.Condition is null || section.Condition.Matches(number)) return section.Style;
            return null;
        }
        var index = number < 0 && sections.Length > 1 ? 1 : number == 0 && sections.Length > 2 ? 2 : 0;
        return sections[index].Style;
    }
    private static CellStyle ClassifySection(long id, string? format)
    {
        if (format is not null)
        {
            var unquoted = new StringBuilder();
            var quoted = false;
            var duration = false;
            for (var i = 0; i < format.Length; i++)
            {
                var character = char.ToLowerInvariant(format[i]);
                if (character == '"') { quoted = !quoted; continue; }
                if (quoted) continue;
                if (character == ';') break;
                if (character is '\\' or '_' or '*')
                {
                    if (++i >= format.Length) return new CellStyle(StyleKind.Number);
                    continue;
                }
                if (character == '[')
                {
                    var close = format.IndexOf(']', i + 1);
                    if (close < 0) return new CellStyle(StyleKind.Number);
                    var bracket = format.AsSpan(i + 1, close - i - 1);
                    if (bracket.Length is 1 or 2 && char.ToLowerInvariant(bracket[0]) is 'h' or 'm' or 's' &&
                        (bracket.Length == 1 || char.ToLowerInvariant(bracket[1]) == char.ToLowerInvariant(bracket[0]))) duration = true;
                    i = close;
                    continue;
                }
                unquoted.Append(character);
            }
            if (quoted) return new CellStyle(StyleKind.Number);
            var code = unquoted.ToString();
            var amPm = code.Contains("am/pm", StringComparison.Ordinal) || code.Contains("a/p", StringComparison.Ordinal);
            code = code.Replace("am/pm", "", StringComparison.Ordinal).Replace("a/p", "", StringComparison.Ordinal);
            if (code.Any(c => char.IsAsciiLetter(c) && "ymdhseg".IndexOf(c) < 0)) return new CellStyle(StyleKind.Number);
            if (duration) return new CellStyle(StyleKind.Duration);
            var time = amPm || code.Contains('h') || code.Contains('s');
            var date = code.Contains('y') || code.Contains('d') || code.Contains('g') || (!time && code.Contains('m'));
            if (date || time) return new CellStyle(date ? time ? StyleKind.DateTime : StyleKind.Date : StyleKind.Time);
            if (code.Length > 1 && code.All(c => c == '0')) return new CellStyle(StyleKind.ZeroPadded, code.Length);
            return new CellStyle(StyleKind.Number);
        }
        return new CellStyle(id switch
        {
            >= 14 and <= 17 or >= 27 and <= 31 or >= 34 and <= 36 or >= 50 and <= 58 => StyleKind.Date,
            >= 18 and <= 21 or 32 or 33 or 45 or 47 => StyleKind.Time,
            22 => StyleKind.DateTime,
            46 => StyleKind.Duration,
            _ => StyleKind.Number
        });
    }
    private static string FormatNumber(string raw, CellStyle style, bool date1904)
    {
        if (style.Sections is null && style.Kind == StyleKind.Number || !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number)) return raw;
        var selected = SelectStyle(style, number);
        if (selected is null || selected.Kind == StyleKind.Number) return raw;
        style = selected;
        if (style.Kind == StyleKind.ZeroPadded)
            return decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var integer) && decimal.Truncate(integer) == integer ? integer.ToString(new string('0', Math.Min(style.Width, MaximumCellLength)), CultureInfo.InvariantCulture) : raw;
        try
        {
            if (style.Kind == StyleKind.Duration) return TimeSpan.FromDays(number).ToString("c", CultureInfo.InvariantCulture);
            if (number < 0) return raw;
            var fraction = number - Math.Floor(number);
            var time = TimeSpan.FromDays(fraction).ToString("hh\\:mm\\:ss\\.fffffff", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
            if (style.Kind == StyleKind.Time) return time;
            var date = !date1904 && Math.Floor(number) == 60 ? "1900-02-29" : (date1904 ? new DateTime(1904, 1, 1) : number < 60 ? new DateTime(1899, 12, 31) : new DateTime(1899, 12, 30)).AddDays(Math.Floor(number)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return style.Kind == StyleKind.Date ? date : date + " " + time;
        }
        catch (ArgumentOutOfRangeException) { return raw; }
        catch (OverflowException) { return raw; }
    }
    private static string[] MakeSheetNames(IReadOnlyList<CsvDocument> documents)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return documents.Select((document, index) =>
        {
            var original = document.WorksheetName ?? Path.GetFileNameWithoutExtension(document.DisplayName);
            var cleaned = new string(original.Select(c => c < 32 || ":\\/?*[]".Contains(c) ? '_' : c).ToArray()).Trim('\'');
            if (string.IsNullOrWhiteSpace(cleaned)) cleaned = $"Sheet{index + 1}";
            if (cleaned.Length > 31) cleaned = cleaned[..31];
            var candidate = cleaned;
            var suffix = 2;
            while (!used.Add(candidate))
            {
                var tail = " (" + suffix++.ToString(CultureInfo.InvariantCulture) + ")";
                candidate = cleaned[..Math.Min(cleaned.Length, 31 - tail.Length)] + tail;
            }
            return candidate;
        }).ToArray();
    }
    private static void WriteXml(ZipArchive archive, string path, Action<XmlWriter> action)
    {
        using var stream = archive.CreateEntry(path, CompressionLevel.Fastest).Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = false, NewLineHandling = NewLineHandling.Entitize });
        writer.WriteStartDocument(); action(writer); writer.WriteEndDocument();
    }
    private static void WriteContentType(XmlWriter writer, string ns, string path, string contentType)
    {
        writer.WriteStartElement("Override", ns); writer.WriteAttributeString("PartName", path); writer.WriteAttributeString("ContentType", contentType); writer.WriteEndElement();
    }
    private static void WriteRelationship(XmlWriter writer, string id, string type, string target)
    {
        writer.WriteStartElement("Relationship", PackageRelNs); writer.WriteAttributeString("Id", id); writer.WriteAttributeString("Type", RelNs + "/" + type); writer.WriteAttributeString("Target", target); writer.WriteEndElement();
    }

    private sealed class SharedStrings : IDisposable
    {
        private readonly SqliteConnection connection;
        private readonly SqliteTransaction transaction;
        private readonly CancellationToken token;
        private readonly string table = "__xlsx_strings_" + Guid.NewGuid().ToString("N");
        private readonly SqliteCommand lookup;
        private readonly Dictionary<long, (string Value, LinkedListNode<long> Node)> cache = [];
        private readonly LinkedList<long> recent = [];
        private long cacheBytes;
        public SharedStrings(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
        {
            this.connection = connection; this.transaction = transaction; this.token = token;
            using var create = connection.CreateCommand(); create.Transaction = transaction;
            create.CommandText = $"CREATE TABLE {Workspace.Quote(table)}(id INTEGER PRIMARY KEY,value TEXT NOT NULL)"; create.ExecuteNonQuery();
            lookup = connection.CreateCommand(); lookup.Transaction = transaction; lookup.CommandText = $"SELECT value FROM {Workspace.Quote(table)} WHERE id=$id";
            lookup.Parameters.Add("$id", SqliteType.Integer); lookup.Prepare();
        }
        public void Load(ZipArchiveEntry entry)
        {
            using var reader = OpenXml(entry); RequireRoot(reader, "sst");
            using var insert = connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = $"INSERT INTO {Workspace.Quote(table)}(id,value) VALUES($id,$value)";
            insert.Parameters.Add("$id", SqliteType.Integer); insert.Parameters.Add("$value", SqliteType.Text); insert.Prepare();
            long index = 0;
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "si") continue;
                var value = ReadRichText(reader);
                insert.Parameters[0].Value = index++; insert.Parameters[1].Value = value; insert.ExecuteNonQuery();
            }
        }
        public string Get(long index)
        {
            token.ThrowIfCancellationRequested();
            if (cache.TryGetValue(index, out var found))
            {
                recent.Remove(found.Node); recent.AddLast(found.Node);
                return found.Value;
            }
            lookup.Parameters[0].Value = index;
            var value = lookup.ExecuteScalar() as string ?? throw new InvalidDataException("XLSX の共有文字列参照が不正です。");
            var size = value.Length * 2L + 128;
            while (cache.Count >= 4096 || cacheBytes + size > 2 * 1024 * 1024)
            {
                var oldest = recent.First!;
                cacheBytes -= cache[oldest.Value].Value.Length * 2L + 128;
                cache.Remove(oldest.Value); recent.RemoveFirst();
            }
            cache[index] = (value, recent.AddLast(index)); cacheBytes += size;
            return value;
        }
        public void Drop() { lookup.Dispose(); using var drop = connection.CreateCommand(); drop.Transaction = transaction; drop.CommandText = $"DROP TABLE {Workspace.Quote(table)}"; drop.ExecuteNonQuery(); }
        public void Dispose() => lookup.Dispose();
    }
}
