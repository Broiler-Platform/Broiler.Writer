using System.IO.Compression;
using System.Text;
using Broiler.Documents;
using Broiler.Documents.Model;
using Broiler.Graphics;

namespace Broiler.Writer.FormatCodes.Tests;

/// <summary>
/// End-to-end cover for opening a document in the Writer. The regression this
/// guards is a DOCX whose whole body is one layout table — the shape CV and
/// letterhead templates use — which opened as a blank page with no explanation.
/// </summary>
public sealed class WriterDocumentLoadTests
{
    [Fact(Timeout = 600000)]
    public void Opens_A_Docx_Whose_Body_Is_A_Layout_Table()
    {
        using WriterApp app = CreateApp();
        byte[] bytes = TableDocx("Elene Bartky", "Berufserfahrung");

        using var stream = new MemoryStream(bytes, writable: false);
        Assert.True(app.LoadDocument(stream, "Lebenslauf.docx"));

        string text = app.Document.PlainText;
        Assert.Contains("Elene Bartky", text, StringComparison.Ordinal);
        Assert.Contains("Berufserfahrung", text, StringComparison.Ordinal);
        Assert.DoesNotContain(
            app.LastReadDiagnostics,
            diagnostic => diagnostic.Code == "docx.document.empty");
    }

    [Fact(Timeout = 600000)]
    public void Keeps_The_Read_Diagnostics_Of_The_Last_Open()
    {
        using WriterApp app = CreateApp();
        using var stream = new MemoryStream(TableDocx("cell"), writable: false);
        app.LoadDocument(stream, "table.docx");

        Assert.Contains(app.LastReadDiagnostics, diagnostic => diagnostic.Code == "docx.read.summary");
        Assert.Contains(app.LastReadDiagnostics, diagnostic => diagnostic.Code == "docx.table.flattened");
    }

    [Fact(Timeout = 600000)]
    public void Open_Status_Ignores_Info_Notes()
    {
        var result = new DocumentReadResult(
            RichTextDocument.FromPlainText("text"),
            [DocumentDiagnostic.Info("docx.read.summary", "summary")]);

        Assert.Equal("Opened file.docx", WriterApp.DescribeOpen("file.docx", result));
    }

    [Fact(Timeout = 600000)]
    public void Open_Status_Counts_Warnings_And_Errors()
    {
        var result = new DocumentReadResult(
            RichTextDocument.FromPlainText("text"),
            [
                DocumentDiagnostic.Info("docx.read.summary", "summary"),
                DocumentDiagnostic.Warning("docx.table.flattened", "flattened"),
                DocumentDiagnostic.Warning("docx.skip.embedded", "skipped"),
            ]);

        Assert.Equal("Opened file.docx with 2 note(s)", WriterApp.DescribeOpen("file.docx", result));
    }

    [Fact(Timeout = 600000)]
    public void Open_Status_Calls_Out_A_Document_With_No_Readable_Content()
    {
        var result = new DocumentReadResult(
            RichTextDocument.Empty,
            [DocumentDiagnostic.Warning("docx.document.empty", "empty")]);

        Assert.Equal(
            "Opened file.docx (no readable content) with 1 note(s)",
            WriterApp.DescribeOpen("file.docx", result));
    }

    private static WriterApp CreateApp()
    {
        var host = new WriterUiHost(
            () => new BSize(1200, 800),
            () => 1,
            () => { },
            _ => { });
        return new WriterApp(host, () => { });
    }

    /// <summary>A DOCX package whose body holds nothing but a one-row table.</summary>
    private static byte[] TableDocx(params string[] cells)
    {
        var body = new StringBuilder("<w:tbl><w:tblPr/><w:tblGrid/><w:tr>");
        foreach (string cell in cells)
            body.Append("<w:tc><w:tcPr/><w:p><w:r><w:t>").Append(cell).Append("</w:t></w:r></w:p></w:tc>");
        body.Append("</w:tr></w:tbl><w:p/>");

        var parts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["[Content_Types].xml"] =
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Override PartName=\"/word/document.xml\" ContentType=\"" +
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
                "</Types>",
            ["_rels/.rels"] =
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" " +
                "Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" " +
                "Target=\"word/document.xml\"/>" +
                "</Relationships>",
            ["word/document.xml"] =
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
                "<w:body>" + body + "</w:body></w:document>",
        };

        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (KeyValuePair<string, string> part in parts)
            {
                ZipArchiveEntry entry = archive.CreateEntry(part.Key, CompressionLevel.NoCompression);
                using Stream stream = entry.Open();
                byte[] encoded = Encoding.UTF8.GetBytes(part.Value);
                stream.Write(encoded, 0, encoded.Length);
            }
        }

        return buffer.ToArray();
    }
}
