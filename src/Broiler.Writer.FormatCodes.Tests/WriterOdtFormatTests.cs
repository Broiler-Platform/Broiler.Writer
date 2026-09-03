using Broiler.Documents;
using Broiler.Documents.Model;
using Broiler.Documents.Odt;
using Broiler.Graphics;
using Broiler.UI.FileDialog;

namespace Broiler.Writer.FormatCodes.Tests;

/// <summary>
/// Cover for wiring <c>Broiler.Documents.Odt</c> into the Writer. The codec was
/// implemented, tested and registered in the CLI but reached no Writer head, so
/// the one word-processor format the Writer could round-trip without a licence
/// question was the one format it did not offer.
/// </summary>
/// <remarks>
/// ODT sits in the shared default set rather than in a per-head registration,
/// which is the opposite of PDF and for the opposite reason: PDF is held out so
/// that Android and WebAssembly cannot acquire it before their gates pass
/// (<see cref="WriterPdfFormatTests"/>), whereas ODT takes no dependency beyond
/// the ZIP and XML stack DOCX already uses, so no head has a gate to pass for it.
/// </remarks>
public sealed class WriterOdtFormatTests
{
    [Fact(Timeout = 600000)]
    public void Every_Head_Carries_Odt_For_Opening_And_Saving()
    {
        WriterDocumentFormats formats = WriterDocumentFormats.CreateDefault();

        Assert.Contains(formats.Formats, format => format.MatchesExtension(".odt"));
        Assert.NotNull(formats.FindForSave(".odt"));
        Assert.Contains(formats.CreateOpenFilters(), FilterMentionsOdt);
        Assert.Contains(formats.CreateSaveFilters(), FilterMentionsOdt);
        Assert.Contains(".odt", formats.DescribeSaveExtensions());
    }

    [Fact(Timeout = 600000)]
    public void Rtf_Is_Still_The_Extension_An_Unnamed_Save_Completes_With()
    {
        // DefaultExtension is the first savable format's extension, so inserting
        // ODT ahead of RTF would silently rename every untitled save.
        Assert.Equal(".rtf", WriterDocumentFormats.CreateDefault().DefaultExtension);
    }

    [Fact(Timeout = 600000)]
    public void Opens_An_Odt_Through_The_Registered_Codec()
    {
        using WriterApp app = CreateApp();

        using var stream = new MemoryStream(WriteOdt("Quarterly report", "Prepared by the Writer."), writable: false);
        Assert.True(app.LoadDocument(stream, "report.odt"));

        string text = app.Document.PlainText;
        Assert.Contains("Quarterly report", text, StringComparison.Ordinal);
        Assert.Contains("Prepared by the Writer.", text, StringComparison.Ordinal);
    }

    [Fact(Timeout = 600000)]
    public void An_Odt_Is_Recognized_By_Its_Content_Without_The_Filename()
    {
        // ODF puts an uncompressed `mimetype` first in the package for exactly
        // this, so the probe should not need the hint the open dialog supplies.
        using WriterApp app = CreateApp();

        using var stream = new MemoryStream(WriteOdt("Recognized by content"), writable: false);
        Assert.True(app.LoadDocument(stream, "report.bin"));

        Assert.Contains("Recognized by content", app.Document.PlainText, StringComparison.Ordinal);
    }

    [Fact(Timeout = 600000)]
    public void Saves_An_Odt_The_Codec_Can_Read_Back()
    {
        using WriterApp app = CreateApp();

        using var source = new MemoryStream(WriteOdt("Round trip", "Second paragraph."), writable: false);
        Assert.True(app.LoadDocument(source, "report.odt"));

        using var destination = new MemoryStream();
        Assert.True(app.WriteDocument(destination, "saved.odt"));
        Assert.True(destination.Length > 0);

        // Read back through the codec rather than asserting on bytes: what matters
        // is that the Writer's save produced a package the reader accepts.
        destination.Position = 0;
        DocumentReadResult result = new OdtDocumentCodec().Read(destination);
        Assert.Equal(DocumentResultStatus.Success, result.Status);

        string text = result.Document!.PlainText;
        Assert.Contains("Round trip", text, StringComparison.Ordinal);
        Assert.Contains("Second paragraph.", text, StringComparison.Ordinal);
    }

    private static bool FilterMentionsOdt(UiFileDialogFilter filter) =>
        filter.Pattern.Contains("*.odt", StringComparison.OrdinalIgnoreCase);

    private static byte[] WriteOdt(params string[] paragraphs)
    {
        RichTextDocument document = RichTextDocument.FromPlainText(string.Join("\n", paragraphs));
        using var buffer = new MemoryStream();
        DocumentWriteResult result = new OdtDocumentCodec().Write(document, buffer);
        Assert.Equal(DocumentResultStatus.Success, result.Status);
        return buffer.ToArray();
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
}
