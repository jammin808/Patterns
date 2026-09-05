using System.IO.Compression;
using System.Text;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>CSV and the first sheet of an .xlsx, read into a header-keyed table.</summary>
public class TableFileTests
{
    [Fact]
    public void CsvHonoursQuotesLineBreaksSeparatorsAndTheBom()
    {
        var text = "﻿Number,Name,Notes\r\n01.010,\"Walk-in, doors\",\"Two lines\nof notes\"\r\n\r\n01.020,Keynote,\"She said \"\"go\"\"\"\n";
        var t = CsvTable.Parse(text);
        Assert.Equal(new[] { "Number", "Name", "Notes" }, t.Headers);
        Assert.Equal(2, t.Rows.Count);
        Assert.Equal("Walk-in, doors", t.Get(0, "name"));
        Assert.Equal("Two lines\nof notes", t.Get(0, "NOTES"));
        Assert.Equal("She said \"go\"", t.Get(1, "Notes"));
        Assert.Equal("", t.Get(1, "Missing"));
        Assert.Equal("", t.Get(5, "Name"));
        Assert.Equal(1, t.Column("nope", "Name"));

        // Semicolons (a European Excel export) and tabs are told apart on the header line.
        var semi = CsvTable.Parse("Number;Name\n1;\"a;b\"\n");
        Assert.Equal("a;b", semi.Get(0, "Name"));
        var tabs = CsvTable.Parse("Number\tName\n1\tTabbed\n");
        Assert.Equal("Tabbed", tabs.Get(0, "Name"));
        Assert.Equal(TableData.Empty, CsvTable.Parse(""));
        Assert.Empty(CsvTable.Parse("\n\n").Headers);

        // Out and back again.
        var written = CsvTable.Write(new[] { new[] { "Number", "Name" }, new[] { "01.010", "Walk-in, \"doors\"" } });
        Assert.StartsWith("﻿Number,Name\r\n", written);
        Assert.Equal("Walk-in, \"doors\"", CsvTable.Parse(written).Get(0, "Name"));
    }

    [Fact]
    public void TheFirstSheetOfAnXlsxReadsSharedInlineNumbersAndBooleans()
    {
        var bytes = BuildXlsx(
            sharedStrings: new[] { "Number", "Name", "Look", "Walk-in" },
            sheetXml: "<sheetData>" +
                      "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c><c r=\"C1\" t=\"s\"><v>2</v></c><c r=\"D1\" t=\"inlineStr\"><is><t>Confirm</t></is></c></row>" +
                      "<row r=\"2\"><c r=\"A2\"><v>1.01</v></c><c r=\"B2\" t=\"s\"><v>3</v></c><c r=\"D2\" t=\"b\"><v>1</v></c></row>" +
                      "<row r=\"3\"><c r=\"A3\"><v>2</v></c><c r=\"B3\" t=\"str\"><v>Keynote</v></c><c r=\"C3\" t=\"inlineStr\"><is><t>Bars</t></is></c></row>" +
                      "</sheetData>");
        var t = XlsxTable.Read(bytes);
        Assert.Equal(new[] { "Number", "Name", "Look", "Confirm" }, t.Headers);
        Assert.Equal(2, t.Rows.Count);
        Assert.Equal("1.01", t.Get(0, "Number"));
        Assert.Equal("Walk-in", t.Get(0, "Name"));
        Assert.Equal("", t.Get(0, "Look"));          // the cell was skipped
        Assert.Equal("TRUE", t.Get(0, "Confirm"));
        Assert.Equal("2", t.Get(1, "Number"));       // a whole number reads without ".0"
        Assert.Equal("Keynote", t.Get(1, "Name"));
        Assert.Equal("Bars", t.Get(1, "Look"));
        Assert.Equal(27, XlsxTable.ColumnIndex("AB7"));
        Assert.Equal(-1, XlsxTable.ColumnIndex(null));
    }

    /// <summary>The smallest workbook Excel would recognise: a workbook, its relationships, one sheet and the shared strings.</summary>
    private static byte[] BuildXlsx(string[] sharedStrings, string sheetXml)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "xl/workbook.xml",
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                "<sheets><sheet name=\"Cues\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Add(zip, "xl/_rels/workbook.xml.rels",
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/cues.xml\"/></Relationships>");
            Add(zip, "xl/worksheets/cues.xml",
                "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" + sheetXml + "</worksheet>");
            var sst = new StringBuilder("<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            foreach (var s in sharedStrings) sst.Append("<si><t>").Append(s).Append("</t></si>");
            sst.Append("</sst>");
            Add(zip, "xl/sharedStrings.xml", sst.ToString());
        }
        return ms.ToArray();
    }

    private static void Add(ZipArchive zip, string path, string xml)
    {
        var entry = zip.CreateEntry(path);
        using var s = entry.Open();
        var bytes = Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + xml);
        s.Write(bytes, 0, bytes.Length);
    }
}
