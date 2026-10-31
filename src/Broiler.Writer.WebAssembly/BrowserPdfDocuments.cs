using Broiler.Documents.Pdf;
using Broiler.Documents.Pdf.Images;
using Broiler.Documents.Pdf.Fonts;

namespace Broiler.Writer.WebAssembly;

/// <summary>Browser-only, managed PDF import composition. PDF export is not registered.</summary>
internal static class BrowserPdfDocuments
{
    internal static PdfDocumentCodec CreateCodec() => new(
        PdfCodecServices.Base
            .WithStreamFilters(new JpegStreamFilter())
            .WithUriPolicy(new PdfUriPolicy(allowHttp: true, allowMailto: true))
            .WithFontProgramReader(new GraphicsFontProgramReader())
            .WithColorProfileReader(new IccColorProfileReader()));
}
