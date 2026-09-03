using System.Text;
using Broiler.Documents;
using Broiler.Documents.Model;
using Broiler.Documents.Pdf;
using Broiler.Graphics;
using Broiler.UI.FileDialog;
using System.Globalization;
using Broiler.Media.Image.Managed;
using Broiler.Media.Image;
using Broiler.Documents.Pdf.Images;
using Broiler.Documents.Pdf.Filters;

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
        WriterDocumentFormats formats = DesktopFormats();
        using WriterApp app = CreateApp(formats);

        // Derived from the set rather than listed, so a format added to the shared
        // defaults is covered here instead of quietly staying untested.
        string[] advertised = formats.Formats
            .Where(format => format.CanSave)
            .Select(format => format.DefaultExtension)
            .ToArray();
        Assert.Contains(".odt", advertised);

        foreach (string extension in advertised)
        {
            using var destination = new MemoryStream();
            Assert.True(app.WriteDocument(destination, "document" + extension));
            Assert.True(destination.Length > 0, extension + " produced no bytes");
        }
    }

    /// <summary>What the Windows and Linux heads compose.</summary>
    /// <summary>
    /// The PDF service graph both desktop heads compose.
    /// </summary>
    /// <remarks>
    /// A hand-copy of the heads', deliberately: a shared catalog the tests and
    /// every head could read from is exactly how PDF would reach Android and
    /// WebAssembly without either head asking (§10.1). The copy is what
    /// <see cref="The_Desktop_Composition_Decodes_Jpeg_And_Nothing_Else"/> keeps
    /// honest.
    /// </remarks>
    private static PdfCodecServices DesktopPdfServices() =>
        PdfCodecServices.Base.WithStreamFilters(new JpegStreamFilter());

    private static WriterDocumentFormats DesktopFormats() =>
        WriterDocumentFormats.CreateDefault().With(
            new WriterDocumentFormat(
                new PdfDocumentCodec(DesktopPdfServices()),
                "PDF",
                WriterFormatCapabilities.Open));


    [Fact(Timeout = 600000)]
    public void The_Desktop_Composition_Decodes_Jpeg_And_Nothing_Else()
    {
        PdfCodecServices desktop = DesktopPdfServices();

        Assert.True(desktop.SupportsFilter(PdfFilterNames.Dct));

        // Not because the others are uncleared. IP-008 approved JBIG2 and IP-009
        // retired the fax patent position — but both decode paths rest on
        // SRC-017, which is pending, and a pending row still blocks. Neither goes
        // into anything that ships until it is answered.
        Assert.False(desktop.SupportsFilter(PdfFilterNames.CcittFax));
        Assert.False(desktop.SupportsFilter(PdfFilterNames.Jbig2));

        // JPEG 2000 has no entropy decoder to compose at all.
        Assert.False(desktop.SupportsFilter(PdfFilterNames.Jpx));

        // Linking the assembly is not composing its filters, and the decision
        // stays each head's: a build that composes nothing still decodes nothing.
        Assert.False(PdfCodecServices.Base.SupportsFilter(PdfFilterNames.Dct));
    }

    [Fact(Timeout = 600000)]
    public void A_Jpeg_In_A_Pdf_Reaches_The_Document()
    {
        // What composing the decoder is for, and what it did not do before the
        // §6.2 conversion context landed: the samples are decoded, admitted by
        // the read policy, and projected into the model as a picture the Writer
        // can draw.
        byte[] pdf = PdfWithJpeg(32, 16);

        using var stream = new MemoryStream(pdf, writable: false);
        DocumentReadResult result = new PdfDocumentCodec(DesktopPdfServices()).Read(
            stream,
            new DocumentReadOptions(resourcePolicy: DocumentResourcePolicy.AllowOwnDocuments));

        Assert.True(result.IsUsable);
        InlineImage image = Assert.Single(ImagesIn(result.Document));
        Assert.Equal(32, image.Resource.PixelWidth);
        Assert.Equal(16, image.Resource.PixelHeight);
        Assert.DoesNotContain(
            result.Diagnostics,
            d => d.Code == "pdf.image.dct.tuple-unsupported" || d.Code == "pdf.image.not-composed");
    }

    [Fact(Timeout = 600000)]
    public void Without_The_Decoder_The_Same_File_Reports_Instead()
    {
        // The other half of the boundary. The base graph composes no image
        // filter, so the same document reads as text and says what it skipped
        // rather than quietly losing it.
        byte[] pdf = PdfWithJpeg(32, 16);

        using var stream = new MemoryStream(pdf, writable: false);
        DocumentReadResult result = new PdfDocumentCodec().Read(
            stream,
            new DocumentReadOptions(resourcePolicy: DocumentResourcePolicy.AllowOwnDocuments));

        Assert.Empty(ImagesIn(result.Document));

        // And it names the filter and the exact tuple rather than saying an
        // image went missing, which is what lets a reader decide whether
        // composing a decoder would have helped.
        DocumentDiagnostic skipped = Assert.Single(
            result.Diagnostics.Where(d => d.Code == "pdf.image.dct.tuple-unsupported"));
        Assert.Contains("composes no image decoder", skipped.Message, StringComparison.Ordinal);
        Assert.Contains("32x16 8bpc DeviceRGB DCTDecode", skipped.Message, StringComparison.Ordinal);
    }

    private static List<InlineImage> ImagesIn(RichTextDocument document)
    {
        var images = new List<InlineImage>();
        foreach (RichTextParagraph paragraph in document.Paragraphs)
        {
            foreach (StyleRun run in paragraph.Runs)
            {
                if (run.Style.Image is InlineImage image)
                    images.Add(image);
            }
        }

        return images;
    }


    /// <summary>
    /// A one-page PDF drawing a JPEG, assembled object by object.
    /// </summary>
    /// <remarks>
    /// The PDF writer emits no images, so a document containing one has to be
    /// built here. Nothing is committed: the JPEG is encoded in the test by this
    /// repository's own codec, and the file around it is seven objects and a
    /// cross-reference table.
    /// </remarks>
    private static byte[] PdfWithJpeg(int width, int height)
    {
        byte[] rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = ((y * width) + x) * 4;
                rgba[i] = (byte)(x * 255 / Math.Max(1, width - 1));
                rgba[i + 1] = (byte)(y * 255 / Math.Max(1, height - 1));
                rgba[i + 2] = 96;
                rgba[i + 3] = 255;
            }
        }

        byte[] jpeg = new JpegImageCodec().Encode(new ImageBuffer(width, height, rgba), quality: 90);
        string content = string.Create(
            CultureInfo.InvariantCulture,
            $"q {width} 0 0 {height} 40 700 cm /Im0 Do Q");

        var objects = new List<byte[]>
        {
            Latin1("<< /Type /Catalog /Pages 2 0 R >>"),
            Latin1("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Latin1(
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(Latin1("<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture) + " >>"), Latin1(content)),
            Stream(
                Latin1(string.Create(
                    CultureInfo.InvariantCulture,
                    $"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} " +
                    $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>")),
                jpeg),
        };

        var file = new List<byte>();
        void Append(string text) => file.AddRange(Latin1(text));

        Append("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(file.Count);
            Append((i + 1).ToString(CultureInfo.InvariantCulture) + " 0 obj\n");
            file.AddRange(objects[i]);
            Append("\nendobj\n");
        }

        int startXref = file.Count;
        Append("xref\n0 " + (objects.Count + 1).ToString(CultureInfo.InvariantCulture) + "\n");
        Append("0000000000 65535 f \n");
        foreach (int offset in offsets)
            Append(offset.ToString(CultureInfo.InvariantCulture).PadLeft(10, '0') + " 00000 n \n");

        Append(
            "trailer\n<< /Size " + (objects.Count + 1).ToString(CultureInfo.InvariantCulture) +
            " /Root 1 0 R >>\nstartxref\n" + startXref.ToString(CultureInfo.InvariantCulture) + "\n%%EOF");

        return file.ToArray();
    }

    private static byte[] Stream(byte[] dictionary, byte[] data)
    {
        var bytes = new List<byte>(dictionary);
        bytes.AddRange(Latin1("\nstream\n"));
        bytes.AddRange(data);
        bytes.AddRange(Latin1("\nendstream"));
        return bytes.ToArray();
    }

    private static byte[] Latin1(string text) => Encoding.Latin1.GetBytes(text);

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
