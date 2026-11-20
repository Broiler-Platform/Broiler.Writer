using System.IO.Compression;
using Broiler.Documents;
using Broiler.Documents.Model;
using Broiler.Documents.Resources;
using Broiler.Documents.Rtf;
using Broiler.Graphics.Geometry;
using Broiler.Graphics.Imaging;

namespace Broiler.Writer.FormatCodes.Tests;

public sealed class WriterRecoveryCheckpointTests
{
    private static readonly string LongListItem = new('x', 16384);
    private static readonly DocumentLimits FallbackLimits = new(maxDocumentBytes: 8192);

    [Theory]
    [InlineData(ListKind.Bullet)]
    [InlineData(ListKind.Numbered)]
    public void Checkpoint_Falls_Back_To_Docx_And_Restores_Simple_Lists(ListKind listKind)
    {
        var document = RichTextDocument.FromParagraphs([
            RichTextParagraph.Plain(LongListItem).WithParagraphStyle(ParagraphStyle.Default with { ListKind = listKind }),
            RichTextParagraph.Plain("Second item").WithParagraphStyle(ParagraphStyle.Default with { ListKind = listKind }),
        ]);
        // The repeated text exceeds the RTF byte budget but fits compressed DOCX.
        var options = new DocumentReadOptions(limits: FallbackLimits);
        var checkpoint = WriterRecoveryCheckpoint.Capture(document, new DocumentWriteOptions(), options);
        Assert.Equal(WriterRecoveryCheckpoint.DocxV1, checkpoint.Format);
        DocumentReadResult read = WriterRecoveryCheckpoint.Read(checkpoint.Format, checkpoint.Bytes, options);

        Assert.Equal(document.PlainText, read.Document.PlainText);
        Assert.All(read.Document.Paragraphs, paragraph => Assert.Equal(listKind, paragraph.Style.ListKind));
    }

    [Fact]
    public void Legacy_Rtf_And_Corrupt_Or_Mislabeled_Payloads_Are_Distinguished()
    {
        byte[] legacy = "{\\rtf1\\ansi earlier draft}"u8.ToArray();
        var options = new DocumentReadOptions();
        Assert.Equal("earlier draft", WriterRecoveryCheckpoint.Read(WriterRecoveryCheckpoint.Rtf, legacy, options).Document.PlainText);
        Assert.Throws<InvalidDataException>(() => WriterRecoveryCheckpoint.Read(WriterRecoveryCheckpoint.DocxV1, legacy, options));
        Assert.Throws<InvalidDataException>(() => WriterRecoveryCheckpoint.Read(WriterRecoveryCheckpoint.DocxV1, "PK\u0003\u0004"u8.ToArray(), options));
        Assert.Throws<InvalidDataException>(() => WriterRecoveryCheckpoint.Read("docx-v2", legacy, options));
    }

    [Fact]
    public void Docx_Fallback_Restores_Embedded_Pixels_And_Their_Resource_Grant()
    {
        byte[] samples = [255, 0, 0, 255];
        using var bitmap = new BBitmap(1, 1, samples);
        var resources = new DocumentConversionContextBuilder(DocumentResourcePolicy.AllowOwnDocuments);
        InlineImage image = resources.AdmitImage(
            new InlineImage(BImageResource.FromEncoded(bitmap.Encode(), "image/png", 1, 1), width: 12, height: 12),
            DocumentResourceProvenance.CallerSupplied, DocumentResourceDisposition.Embedded);
        var document = RichTextDocument.FromParagraphs([
            RichTextParagraph.Plain(LongListItem).WithParagraphStyle(ParagraphStyle.Default with { ListKind = ListKind.Bullet }),
            RichTextParagraph.Create(InlineImage.PlaceholderText, InlineStyle.Default with { Image = image }),
        ]);
        var options = new DocumentReadOptions(limits: FallbackLimits, resourcePolicy: DocumentResourcePolicy.AllowOwnDocuments);

        var checkpoint = WriterRecoveryCheckpoint.Capture(document, new DocumentWriteOptions(resources: resources.Build()), options);
        Assert.Equal(WriterRecoveryCheckpoint.DocxV1, checkpoint.Format);
        DocumentReadResult read = WriterRecoveryCheckpoint.Read(checkpoint.Format, checkpoint.Bytes, options);
        InlineImage restored = Assert.IsType<InlineImage>(read.Document.Paragraphs[1].StyleAt(0).Image);
        Assert.True(read.Resources.IsAllowed(restored.ResourceId, DocumentResourceOperations.ByteTransfer, restored.Resource));
        using BBitmap decoded = BBitmap.Decode(restored.Data.Span);
        Assert.Equal(samples, decoded.ToPixelBuffer().Rgba);
    }

    [Fact]
    public void Docx_Recovery_Rejects_Packages_That_Exceed_The_Expansion_Budget()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        using (Stream entry = archive.CreateEntry("word/document.xml", CompressionLevel.SmallestSize).Open())
            entry.Write(new byte[16384]);
        byte[] bytes = stream.ToArray();
        var options = new DocumentReadOptions(limits: new DocumentLimits(maxDocumentBytes: bytes.Length));

        var error = Assert.Throws<InvalidDataException>(() =>
            WriterRecoveryCheckpoint.Read(WriterRecoveryCheckpoint.DocxV1, bytes, options));
        Assert.Contains("expansion limit", error.Message);
    }

    [Fact]
    public void Rtf_Checkpoint_Accepts_Styled_Text_And_Paragraphs()
    {
        var document = RichTextDocument.FromParagraphs([
            RichTextParagraph.Create("Styled café", InlineStyle.Default with { Bold = true, Underline = true },
                ParagraphStyle.Default with { Alignment = TextAlignment.Center, PageBreakBefore = true }),
            RichTextParagraph.Plain("Another paragraph"),
        ]);
        using var stream = new MemoryStream();
        DocumentWriteResult write = RtfWriter.Write(document, stream);
        DocumentReadResult read = RtfReader.Read(stream.ToArray());

        WriterRecoveryCheckpoint.Validate(document, write, read);
    }

    [Theory]
    [InlineData(ListKind.Bullet)]
    [InlineData(ListKind.Numbered)]
    public void Rtf_Checkpoint_Rejects_List_Loss_With_Identical_Text(ListKind listKind)
    {
        var document = RichTextDocument.FromParagraphs([
            RichTextParagraph.Plain("List item").WithParagraphStyle(ParagraphStyle.Default with { ListKind = listKind }),
        ]);
        using var stream = new MemoryStream();
        DocumentWriteResult write = RtfWriter.Write(RichTextDocument.FromPlainText(document.PlainText), stream);
        DocumentReadResult read = RtfReader.Read(stream.ToArray());

        Assert.Equal(document.PlainText, read.Document.PlainText);
        Assert.Equal(ListKind.None, read.Document.Paragraphs[0].Style.ListKind);
        Assert.Contains("list formatting in paragraph 1",
            Assert.Throws<InvalidDataException>(() => WriterRecoveryCheckpoint.Validate(document, write, read)).Message);
    }

    [Fact]
    public void Rtf_Checkpoint_Rejects_Format_Changes_With_Identical_Text()
    {
        var document = RichTextDocument.FromParagraphs([
            RichTextParagraph.Create("Styled café", InlineStyle.Default with { Italic = true }),
        ]);
        using var stream = new MemoryStream();
        DocumentWriteResult write = RtfWriter.Write(RichTextDocument.FromPlainText(document.PlainText), stream);
        DocumentReadResult read = RtfReader.Read(stream.ToArray());

        Assert.Equal(document.PlainText, read.Document.PlainText);
        Assert.Contains("paragraph 1",
            Assert.Throws<InvalidDataException>(() => WriterRecoveryCheckpoint.Validate(document, write, read)).Message);
    }

    [Fact]
    public void Checkpoint_Validation_Rejects_Partial_Writes()
    {
        var document = RichTextDocument.FromPlainText("Draft");
        byte[] validRtf = "{\\rtf1\\ansi Draft}"u8.ToArray();
        DocumentReadResult read = RtfReader.Read(validRtf);
        var partial = new DocumentWriteResult(validRtf.Length,
            [DocumentDiagnostic.Warning("rtf.test.loss", "A property was lost.")], DocumentResultStatus.Partial,
            DocumentDestinationState.Committed);
        Assert.Contains("A property was lost.",
            Assert.Throws<InvalidDataException>(() => WriterRecoveryCheckpoint.Validate(document, partial, read)).Message);
    }

    [Fact]
    public void Writer_Keeps_Last_Good_Checkpoint_When_A_New_Revision_Loses_Formatting()
    {
        var host = new WriterUiHost(() => new BSize(1200, 800), () => 1, () => { }, _ => { });
        using var app = new WriterApp(host, () => { });
        DirectoryInfo directory = Directory.CreateTempSubdirectory();
        try
        {
            string path = Path.Combine(directory.FullName, "checkpoint-v1");
            app.Editor.Document = RichTextDocument.FromPlainText("Earlier draft");
            app.WriteRecoveryCheckpoint(path);
            byte[] earlier = File.ReadAllBytes(path);
            Assert.Equal("Earlier draft", RtfReader.Read(earlier).Document.PlainText);
            app.RestoreRecoveryCheckpoint(path, legacyRtf: true);
            Assert.Equal("Earlier draft", app.Document.PlainText);

            var image = new InlineImage(BImageResource.FromEncoded(new byte[] { 1, 2, 3 }, "image/png", 1, 1));
            var revised = RichTextDocument.FromParagraphs([RichTextParagraph.Create(
                InlineImage.PlaceholderText, InlineStyle.Default with { Image = image })]);
            app.Editor.Document = revised;
            Assert.Throws<InvalidDataException>(() => app.WriteRecoveryCheckpoint(path));
            Assert.Equal(earlier, File.ReadAllBytes(path));
            Assert.Same(revised, app.Document);

            app.Editor.Document = RichTextDocument.FromParagraphs([
                RichTextParagraph.Plain("List item").WithParagraphStyle(ParagraphStyle.Default with { ListKind = ListKind.Bullet }),
            ]);
            app.WriteRecoveryCheckpoint(path);
            app.RestoreRecoveryCheckpoint(path, legacyRtf: false);
            Assert.Equal("List item", app.Document.PlainText);
            Assert.Equal(ListKind.Bullet, app.Document.Paragraphs[0].Style.ListKind);
            Assert.True(app.IsModified);
            Assert.Equal(path, Assert.Single(Directory.GetFiles(directory.FullName)));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
