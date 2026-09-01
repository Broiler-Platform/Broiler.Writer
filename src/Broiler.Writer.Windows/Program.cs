using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Broiler.Writer;

/// <summary>Windows entry point for Broiler Writer.</summary>
[SupportedOSPlatform("windows7.0")]
internal static class Program
{
    [STAThread]
    private static int Main()
    {
        _ = SetProcessDpiAwarenessContext(new IntPtr(-4)); // PER_MONITOR_AWARE_V2, best effort.

        // Composition root: without a codec catalog the renderer cannot decode
        // the images a document embeds, and the editor would draw every picture
        // as an empty outline.
        Broiler.Graphics.BImageCodecs.Use(
            new Broiler.Media.MediaCodecCatalog(Broiler.Media.Image.Managed.ManagedImageCodecs.CreateCodecs()));

        try
        {
            using var window = new WriterWindow(CreateDocumentFormats());
            return window.Run();
        }
        catch (Exception ex)
        {
            MessageBox(IntPtr.Zero, ex.ToString(), "Broiler Writer", MbIconError | MbOk);
            return 1;
        }
    }

    /// <summary>
    /// The document formats this head offers: the shared four, plus PDF for
    /// opening.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PDF is registered here rather than in <c>Broiler.Writer.Core</c>
    /// deliberately. Putting it in the shared core would hand it to every head
    /// that references the core — including the Android and WebAssembly Writers,
    /// whose package-size, memory, trimming and AOT gates it has not passed — and
    /// a codec must not reach a head by being someone else's transitive reference
    /// (PDF roadmap §10.1). Each head that wants it says so, here.
    /// </para>
    /// <para>
    /// Opening only. <c>PdfDocumentCodec</c> implements writing, but PDF export
    /// has its own release gate that has not been passed, so this head offers no
    /// PDF save filter and its save dispatch has no PDF entry.
    /// </para>
    /// </remarks>
    private static WriterDocumentFormats CreateDocumentFormats() =>
        WriterDocumentFormats.CreateDefault().With(
            new WriterDocumentFormat(
                new Broiler.Documents.Pdf.PdfDocumentCodec(CreatePdfCodecServices()),
                "PDF",
                WriterFormatCapabilities.Open));

    /// <summary>
    /// The PDF codec's service graph: the base composition plus the reviewed
    /// managed JPEG decoder (PDF roadmap §10.1, "the composed managed JPEG
    /// service").
    /// </summary>
    /// <remarks>
    /// <para>
    /// The codec discovers nothing, so a decoder it is not handed is a decoder
    /// it does not have. Until IP-005 cleared baseline sequential DCT this head
    /// had nothing cleared to hand it, and every JPEG a page drew was reported
    /// as skipped for want of a decoder that did not exist.
    /// </para>
    /// <para>
    /// <strong>What this does not do.</strong> It does not put images in the
    /// document. Extraction into the model waits on the shared resource policy
    /// (PDF roadmap §6.2), so a decoded image is reported as decoded and not
    /// projected rather than skipped for want of a decoder. The gain is an
    /// accurate diagnostic and a decode path that is exercised rather than
    /// dormant; the picture still does not reach the page.
    /// </para>
    /// <para>
    /// <strong>Why the JPEG decoder specifically.</strong> Only
    /// <c>DCTDecode</c> is composed. Referencing
    /// <c>Broiler.Documents.Pdf.Images</c> links its CCITT, JBIG2 and JPX
    /// adapters too, and none of them is put into this graph — composing a
    /// filter is what enables it, not linking the assembly that holds it.
    /// </para>
    /// <para>
    /// <strong>On the residual security condition.</strong> The
    /// <c>Broiler.Graphics</c> human review records that its managed image
    /// codecs are security-sensitive and should not process untrusted input
    /// without resource limits and further review, and
    /// <c>JpegStreamFilter</c> states that it supplies the limits and not the
    /// review. This head already installs those codecs globally for the
    /// renderer a few lines up, and already decodes the JPEGs a DOCX or ODT
    /// embeds through them, so composing this filter adds no decoder and no new
    /// class of untrusted input — it routes one more source through the entry
    /// point that checks a frame header against a byte ceiling before decoding
    /// and turns a decoder fault into a skipped image. The review condition
    /// stands for the codecs themselves and is tracked where it is recorded.
    /// </para>
    /// </remarks>
    private static Broiler.Documents.Pdf.PdfCodecServices CreatePdfCodecServices() =>
        Broiler.Documents.Pdf.PdfCodecServices.Base.WithStreamFilters(
            new Broiler.Documents.Pdf.Images.JpegStreamFilter());

    private const uint MbOk = 0x00000000;
    private const uint MbIconError = 0x00000010;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hwnd, string text, string caption, uint type);
}
