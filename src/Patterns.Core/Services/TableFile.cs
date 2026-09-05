using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Patterns.Core.Services;

/// <summary>A sheet as text: a header row and the rows under it. Lookups by header are case-insensitive and trimmed.</summary>
public sealed class TableData
{
    public IReadOnlyList<string> Headers { get; }
    public IReadOnlyList<IReadOnlyList<string>> Rows { get; }

    public TableData(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        Headers = headers;
        Rows = rows;
    }

    public static readonly TableData Empty = new(Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>());

    /// <summary>The first row that has anything in it is the header; the rest are the rows (blank rows dropped).</summary>
    public static TableData FromRows(IEnumerable<IReadOnlyList<string>> rows)
    {
        IReadOnlyList<string>? headers = null;
        var body = new List<IReadOnlyList<string>>();
        foreach (var row in rows)
        {
            if (row.All(string.IsNullOrWhiteSpace)) continue;
            if (headers is null)
            {
                headers = row.Select(h => (h ?? "").Trim()).ToList();
                continue;
            }
            body.Add(row);
        }
        return headers is null ? Empty : new TableData(headers, body);
    }

    /// <summary>The column index of a header (case-insensitive; the first of several names that exists), or -1.</summary>
    public int Column(params string[] names)
    {
        foreach (var name in names)
        {
            for (var i = 0; i < Headers.Count; i++)
            {
                if (string.Equals(Headers[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            }
        }
        return -1;
    }

    /// <summary>A cell by row and header names; "" when the column or the cell is missing.</summary>
    public string Get(int row, params string[] names)
    {
        var col = Column(names);
        if (col < 0 || row < 0 || row >= Rows.Count) return "";
        var cells = Rows[row];
        return col < cells.Count ? (cells[col] ?? "").Trim() : "";
    }
}

/// <summary>CSV in and out: RFC 4180 quoting, any of comma, semicolon or tab (told apart on the header line), a BOM ignored.</summary>
public static class CsvTable
{
    public static TableData Parse(string text, char? separator = null)
    {
        if (string.IsNullOrEmpty(text)) return TableData.Empty;
        if (text[0] == '﻿') text = text[1..];
        var sep = separator ?? Detect(text);
        return TableData.FromRows(Rows(text, sep));
    }

    /// <summary>The separator the header line uses: the most frequent of comma, semicolon and tab outside quotes.</summary>
    public static char Detect(string text)
    {
        var firstLine = text.Split('\n', 2)[0];
        int commas = 0, semis = 0, tabs = 0;
        var quoted = false;
        foreach (var ch in firstLine)
        {
            if (ch == '"') quoted = !quoted;
            if (quoted) continue;
            if (ch == ',') commas++;
            else if (ch == ';') semis++;
            else if (ch == '\t') tabs++;
        }
        if (tabs > commas && tabs > semis) return '\t';
        if (semis > commas) return ';';
        return ',';
    }

    /// <summary>Every record, quotes and embedded line breaks honoured.</summary>
    public static IEnumerable<IReadOnlyList<string>> Rows(string text, char sep)
    {
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        cell.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    cell.Append(ch);
                }
                continue;
            }
            if (ch == '"' && cell.Length == 0)
            {
                quoted = true;
            }
            else if (ch == sep)
            {
                row.Add(cell.ToString());
                cell.Clear();
            }
            else if (ch == '\r')
            {
                // A CR on its own or before an LF ends the record either way.
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString());
                cell.Clear();
                yield return row;
                row = new List<string>();
            }
            else if (ch == '\n')
            {
                row.Add(cell.ToString());
                cell.Clear();
                yield return row;
                row = new List<string>();
            }
            else
            {
                cell.Append(ch);
            }
        }
        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            yield return row;
        }
    }

    /// <summary>Rows as CSV text, quoting what needs it; a BOM up front so Excel opens it as UTF-8.</summary>
    public static string Write(IEnumerable<IEnumerable<string>> rows, char sep = ',')
    {
        var sb = new StringBuilder("﻿");
        foreach (var row in rows)
        {
            var first = true;
            foreach (var cell in row)
            {
                if (!first) sb.Append(sep);
                first = false;
                sb.Append(Quote(cell ?? "", sep));
            }
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    private static string Quote(string cell, char sep)
    {
        if (cell.IndexOfAny(new[] { sep, '"', '\r', '\n' }) < 0) return cell;
        return "\"" + cell.Replace("\"", "\"\"") + "\"";
    }
}

/// <summary>
/// The first worksheet of an .xlsx, read straight from the zip: shared and inline strings,
/// numbers, booleans and formula results as text. No library, no styles, no dates beyond the
/// serial number Excel stores — enough for a cue sheet typed into Excel.
/// </summary>
public static class XlsxTable
{
    public static TableData Read(Stream file)
    {
        using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        var shared = ReadSharedStrings(zip);
        var sheet = FirstSheet(zip);
        if (sheet is null) return TableData.Empty;
        using var stream = sheet.Open();
        return TableData.FromRows(ReadRows(stream, shared));
    }

    public static TableData Read(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        return Read(ms);
    }

    private static ZipArchiveEntry? FirstSheet(ZipArchive zip)
    {
        // The workbook names its first sheet through the relationships; sheet1.xml is the usual answer.
        try
        {
            var workbook = zip.GetEntry("xl/workbook.xml");
            var rels = zip.GetEntry("xl/_rels/workbook.xml.rels");
            if (workbook is not null && rels is not null)
            {
                string? firstRel = null;
                using (var s = workbook.Open())
                using (var reader = XmlReader.Create(s, new XmlReaderSettings { IgnoreWhitespace = true }))
                {
                    while (reader.Read())
                    {
                        if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "sheet")
                        {
                            firstRel = reader.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
                            break;
                        }
                    }
                }
                if (firstRel is not null)
                {
                    using var s = rels.Open();
                    using var reader = XmlReader.Create(s, new XmlReaderSettings { IgnoreWhitespace = true });
                    while (reader.Read())
                    {
                        if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Relationship" && reader.GetAttribute("Id") == firstRel)
                        {
                            var target = reader.GetAttribute("Target") ?? "";
                            var path = target.StartsWith('/') ? target[1..] : "xl/" + target;
                            var entry = zip.GetEntry(path);
                            if (entry is not null) return entry;
                        }
                    }
                }
            }
        }
        catch
        {
            // Fall through to the usual name.
        }
        return zip.GetEntry("xl/worksheets/sheet1.xml");
    }

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var list = new List<string>();
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return list;
        using var s = entry.Open();
        using var reader = XmlReader.Create(s, new XmlReaderSettings { IgnoreWhitespace = true });
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si") list.Add(TextOf(reader));
        }
        return list;
    }

    /// <summary>
    /// The text of a string item or an inline string: every &lt;t&gt; run joined, phonetic guides
    /// left out. Reads the element's subtree, so the outer reader is left on its end tag.
    /// </summary>
    private static string TextOf(XmlReader outer)
    {
        using var sub = outer.ReadSubtree();
        var sb = new StringBuilder();
        // ReadElementContentAsString and Skip both leave the reader on the node after the one
        // consumed, so that node has to be looked at before the next Read().
        var pending = false;
        while (pending || sub.Read())
        {
            pending = false;
            if (sub.NodeType != XmlNodeType.Element) continue;
            if (sub.LocalName == "t")
            {
                sb.Append(sub.ReadElementContentAsString());
                pending = true;
            }
            else if (sub.LocalName == "rPh")
            {
                sub.Skip();
                pending = true;
            }
        }
        return sb.ToString();
    }

    private static IEnumerable<IReadOnlyList<string>> ReadRows(Stream sheet, List<string> shared)
    {
        using var reader = XmlReader.Create(sheet, new XmlReaderSettings { IgnoreWhitespace = true });
        List<string>? row = null;
        string? type = null;
        var column = -1;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "row":
                        row = new List<string>();
                        break;
                    case "c":
                        type = reader.GetAttribute("t");
                        column = ColumnIndex(reader.GetAttribute("r"));
                        if (reader.IsEmptyElement && row is not null) Put(row, column, "");
                        break;
                    case "v":
                    {
                        var raw = reader.ReadElementContentAsString();
                        var text = type switch
                        {
                            "s" => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i >= 0 && i < shared.Count ? shared[i] : "",
                            "b" => raw == "1" ? "TRUE" : "FALSE",
                            _ => Number(raw),
                        };
                        if (row is not null) Put(row, column, text);
                        break;
                    }
                    case "t" when type == "inlineStr":
                    {
                        var text = reader.ReadElementContentAsString();
                        if (row is not null) Put(row, column, text);
                        break;
                    }
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "row" && row is not null)
            {
                yield return row;
                row = null;
            }
        }
    }

    private static string Number(string raw)
    {
        // "12.0" reads as "12"; a serial date stays as its number, which a caller can still read.
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && Math.Abs(d - Math.Round(d)) < 1e-9 && Math.Abs(d) < 1e15)
        {
            return ((long)Math.Round(d)).ToString(CultureInfo.InvariantCulture);
        }
        return raw;
    }

    private static void Put(List<string> row, int column, string text)
    {
        if (column < 0) column = row.Count;
        while (row.Count <= column) row.Add("");
        row[column] = text;
    }

    /// <summary>"A1" → 0, "AB7" → 27; -1 when the reference is missing.</summary>
    public static int ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return -1;
        var n = 0;
        foreach (var ch in reference)
        {
            if (ch < 'A' || ch > 'Z') break;
            n = n * 26 + (ch - 'A' + 1);
        }
        return n - 1;
    }
}
