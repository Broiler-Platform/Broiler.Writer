using Broiler.Documents;
using Broiler.Documents.Model;
using Broiler.Documents.Pdf;
using Broiler.Writer.WebAssembly;

namespace Broiler.Writer.FormatCodes.Tests;

public sealed class BrowserPdfTests
{
    [Fact]
    public void Browser_codec_imports_pdf_text()
    {
        using var bytes = new MemoryStream();
        new PdfDocumentCodec().Write(RichTextDocument.FromPlainText("Browser PDF import"), bytes);
        bytes.Position = 0;
        var result = BrowserPdfDocuments.CreateCodec().Read(bytes, new DocumentReadOptions(
            limits: new DocumentLimits(maxDocumentBytes: 8 * 1024 * 1024)));
        Assert.NotEqual(DocumentResultStatus.Rejected, result.Status);
        Assert.Contains("Browser PDF import", result.Document.PlainText);
    }

    [Fact]
    public void Decoded_pdf_images_share_the_browser_pixel_budget()
    {
        long pixels = 0;
        BrowserImageBudget.Add(4000, 4000, ref pixels);
        BrowserImageBudget.Add(4000, 4000, ref pixels);
        Assert.Equal(BrowserImageBudget.MaximumDocumentPixels, pixels);
        Assert.Throws<NotSupportedException>(() => BrowserImageBudget.Add(1, 1, ref pixels));
        Assert.Throws<NotSupportedException>(() => BrowserImageBudget.Add(8193, 1, ref pixels));
    }
}
