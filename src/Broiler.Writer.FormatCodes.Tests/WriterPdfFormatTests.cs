using System.Text;
using Broiler.Documents;
using Broiler.Documents.Model;
using Broiler.Documents.Pdf;
using Broiler.Documents.Pdf.Filters;
using Broiler.Documents.Pdf.Images;
using Broiler.Graphics;
using Broiler.UI.FileDialog;

namespace Broiler.Writer.FormatCodes.Tests;

/// <summary>
/// Cover for wiring <c>Broiler.Documents.Pdf</c> into the Writer: PDF reaches a
/// head only when that head registers it, it is offered for opening and not for
/// saving, and a read the codec refuses does not cost the user the document they
/// had open.
/// </summary>
public sealed class WriterPdfFormatTests
{
    [Fact(Timeout = 600000)]
    public void The_Shared_Default_Formats_Do_Not_Carry_Pdf()
    {
        // The guard behind "Android and WebAssembly cannot acquire PDF
        // transitively": those heads take the default set, so PDF appearing in it
        // would enable it everywhere at once.
        WriterDocumentFormats formats = WriterDocumentFormats.CreateDefault();

        Assert.DoesNotContain(formats.Formats, format => format.MatchesExtension(".pdf"));
        Assert.Null(formats.FindForSave(".pdf"));
        Assert.DoesNotContain(formats.CreateOpenFilters(), FilterMentionsPdf);
        Assert.DoesNotContain(formats.CreateSaveFilters(), FilterMentionsPdf);
    }

    [Fact(Timeout = 600000)]
    public void Registering_Pdf_Offers_It_For_Opening_Only()
    {
        WriterDocumentFormats formats = DesktopFormats();

        Assert.Contains(formats.CreateOpenFilters(), FilterMentionsPdf);
        Assert.DoesNotContain(formats.CreateSaveFilters(), FilterMentionsPdf);

        // The save dispatch reads the same set the filters come from, so a typed
        // ".pdf" filename cannot reach the writer either.
        Assert.Null(formats.FindForSave(".pdf"));
        Assert.DoesNotContain(".pdf", formats.DescribeSaveExtensions());
    }

    [Fact(Timeout = 600000)]
    public void A_Writer_Composed_Without_A_Format_Set_Gets_The_Default_One()
    {
        using WriterApp bare = CreateApp();
        Assert.DoesNotContain(bare.DocumentFormats.Formats, format => format.MatchesExtension(".pdf"));

        using WriterApp desktop = CreateApp(DesktopFormats());
        Assert.Contains(desktop.DocumentFormats.Formats, format => format.MatchesExtension(".pdf"));
    }

    [Fact(Timeout = 600000)]
    public void Registering_A_Format_For_Saving_Needs_A_Codec_That_Writes()
    {
        // PdfDocumentCodec does implement writing, so this is the check that the
        // capability is a host decision rather than a codec one: it is refused
        // only where the host declines to enable it, not by the type system.
        var format = new WriterDocumentFormat(
            new PdfDocumentCodec(), "PDF", WriterFormatCapabilities.OpenAndSave);
        Assert.True(format.CanSave);

        Assert.Throws<ArgumentException>(() => new WriterDocumentFormats(
        [
            new WriterDocumentFormat(new PdfDocumentCodec(), "PDF", WriterFormatCapabilities.Open),
            new WriterDocumentFormat(new PdfDocumentCodec(), "PDF again", WriterFormatCapabilities.Open),
        ]));
    }

    [Fact(Timeout = 600000)]
    public void Opens_A_Pdf_Through_The_Registered_Codec()
    {
        using WriterApp app = CreateApp(DesktopFormats());

        using var stream = new MemoryStream(WritePdf("Quarterly report", "Prepared by the Writer."), writable: false);
        Assert.True(app.LoadDocument(stream, "report.pdf"));

        string text = app.Document.PlainText;
        Assert.Contains("Quarterly report", text, StringComparison.Ordinal);
        Assert.Contains("Prepared by the Writer.", text, StringComparison.Ordinal);
    }

    [Fact(Timeout = 600000)]
    public void A_Head_That_Did_Not_Register_Pdf_Refuses_One()
    {
        using WriterApp app = CreateApp();
        string before = app.Document.PlainText;

        using var stream = new MemoryStream(WritePdf("Quarterly report"), writable: false);
        Assert.False(app.LoadDocument(stream, "report.pdf"));

        Assert.Equal(before, app.Document.PlainText);
        Assert.Contains("Could not open report.pdf", app.LastAction, StringComparison.Ordinal);
        Assert.Contains("No composed codec recognized the source", app.LastAction, StringComparison.Ordinal);
        Assert.Contains("The open document is unchanged.", app.LastAction, StringComparison.Ordinal);
    }

    [Fact(Timeout = 600000)]
    public void A_Rejected_Read_Leaves_The_Open_Document_Alone()
    {
        using WriterApp app = CreateApp(DesktopFormats());
        string before = app.Document.PlainText;
        Assert.NotEqual(string.Empty, before);

        // A PDF header and then nothing a reader can use: the codec recognizes the
        // file and rejects it, which must not cost the user the open document.
        byte[] bytes = Encoding.ASCII.GetBytes("%PDF-1.7\n% not actually a PDF body\n");
        using var stream = new MemoryStream(bytes, writable: false);
        Assert.False(app.LoadDocument(stream, "broken.pdf"));

        Assert.Equal(before, app.Document.PlainText);
        Assert.Contains("Could not open broken.pdf", app.LastAction, StringComparison.Ordinal);
        Assert.Contains("The open document is unchanged.", app.LastAction, StringComparison.Ordinal);
    }

    [Fact(Timeout = 600000)]
    public void A_Partial_Read_That_Recovered_No_Text_Is_Not_Committed()
    {
        // The shape a scanned PDF produces: the file parsed, every page carried
        // image data and no text layer, and the reader says so. Committing that
        // would replace the open document with a report about the reader.
        Assert.False(WriterApp.MayReplaceDocument(new DocumentReadResult(
            RichTextDocument.Empty,
            [DocumentDiagnostic.Warning(PdfDiagnosticCodes.TextOcrRequired, "no extractable text")],
            DocumentResultStatus.Partial)));

        // A partial read that did recover text is committed; the status line is
        // what says it was partial.
        Assert.True(WriterApp.MayReplaceDocument(new DocumentReadResult(
            RichTextDocument.FromPlainText("page one"),
            [DocumentDiagnostic.Warning(PdfDiagnosticCodes.TextOcrRequired, "page two needs OCR")],
            DocumentResultStatus.Partial)));

        // An empty file that read cleanly is still an empty file, not a failure.
        Assert.True(WriterApp.MayReplaceDocument(new DocumentReadResult(RichTextDocument.Empty)));
    }

    [Fact(Timeout = 600000)]
    public void A_Partial_Open_Is_Never_Reported_As_A_Clean_One()
    {
        string status = WriterApp.DescribeOpen(
            "report.pdf",
            new DocumentReadResult(
                RichTextDocument.FromPlainText("page one"),
                [DocumentDiagnostic.Warning(PdfDiagnosticCodes.TextOcrRequired, "page two needs OCR")],
                DocumentResultStatus.Partial));

        Assert.Equal("Opened report.pdf with 1 note(s); parts of it were skipped or approximated", status);
    }

    [Fact(Timeout = 600000)]
    public void The_Desktop_Composition_Carries_A_Jpeg_Decoder_And_Nothing_Else()
    {
        PdfCodecServices desktop = DesktopPdfCodecServices();

        // IP-005 cleared baseline sequential DCT, so the heads hand the codec a
        // decoder for it and a page drawing a JPEG reports what was decoded
        // rather than a decoder this build does not have.
        Assert.True(desktop.SupportsFilter(PdfFilterNames.Dct));

        // Referencing Broiler.Documents.Pdf.Images links its CCITT, JBIG2 and
        // JPX adapters too. Composing a filter is what enables it, not linking
        // the assembly that holds it, and none of these is composed.
        Assert.False(desktop.SupportsFilter(PdfFilterNames.CcittFax));
        Assert.False(desktop.SupportsFilter(PdfFilterNames.Jbig2));
        Assert.False(desktop.SupportsFilter(PdfFilterNames.Jpx));

        // The decision stays the head's. A build that composes nothing still
        // links no image decoder, which is what the boundary is for.
        Assert.False(PdfCodecServices.Base.SupportsFilter(PdfFilterNames.Dct));
    }

    [Fact(Timeout = 600000)]
    public void Saving_As_Pdf_Is_Refused_Even_Where_Pdf_Opens()
    {
        using WriterApp app = CreateApp(DesktopFormats());

        using var destination = new MemoryStream();
        Assert.False(app.WriteDocument(destination, "report.pdf"));

        Assert.Equal(0, destination.Length);
        Assert.Contains("Unsupported save format '.pdf'", app.LastAction, StringComparison.Ordinal);
    }

    [Fact(Timeout = 600000)]
    public void The_Formats_Still_Save_Every_Format_They_Advertise()
    {
        using WriterApp app = CreateApp(DesktopFormats());

        foreach (string extension in new[] { ".rtf", ".docx", ".html", ".md" })
        {
            using var destination = new MemoryStream();
            Assert.True(app.WriteDocument(destination, "document" + extension));
            Assert.True(destination.Length > 0, extension + " produced no bytes");
        }
    }

    /// <summary>What the Windows and Linux heads compose.</summary>
    /// <remarks>
    /// A hand-copy of both heads' <c>CreateDocumentFormats</c>, and deliberately
    /// so: the roadmap forbids a shared catalog the tests and every head could
    /// read from, because that is exactly how PDF would reach Android and
    /// WebAssembly without either head asking for it (§10.1). The copy is what
    /// <see cref="The_Desktop_Composition_Carries_A_Jpeg_Decoder_And_Nothing_Else"/>
    /// exists to keep honest.
    /// </remarks>
    private static WriterDocumentFormats DesktopFormats() =>
        WriterDocumentFormats.CreateDefault().With(
            new WriterDocumentFormat(
                new PdfDocumentCodec(DesktopPdfCodecServices()),
                "PDF",
                WriterFormatCapabilities.Open));

    /// <summary>The service graph both desktop heads hand the PDF codec.</summary>
    private static PdfCodecServices DesktopPdfCodecServices() =>
        PdfCodecServices.Base.WithStreamFilters(new JpegStreamFilter());

    private static bool FilterMentionsPdf(UiFileDialogFilter filter) =>
        filter.Pattern.Contains("*.pdf", StringComparison.OrdinalIgnoreCase);

    private static byte[] WritePdf(params string[] paragraphs)
    {
        RichTextDocument document = RichTextDocument.FromPlainText(string.Join("\n", paragraphs));
        using var buffer = new MemoryStream();
        DocumentWriteResult result = new PdfDocumentCodec().Write(document, buffer);
        Assert.Equal(DocumentResultStatus.Success, result.Status);
        return buffer.ToArray();
    }

    private static WriterApp CreateApp(WriterDocumentFormats? formats = null)
    {
        var host = new WriterUiHost(
            () => new BSize(1200, 800),
            () => 1,
            () => { },
            _ => { });
        return new WriterApp(host, () => { }, documentFormats: formats);
    }
}
