using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Broiler.Documents.Pdf;
using Broiler.Documents.Pdf.Images;

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
                new Broiler.Documents.Pdf.PdfDocumentCodec(CreatePdfServices()),
                "PDF",
                WriterFormatCapabilities.Open));

    /// <summary>
    /// The PDF service graph this head composes: the JPEG decoder, and nothing
    /// else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Composing a filter is what puts a picture on the page. Since the §6.2
    /// conversion context landed, a decoded image is admitted by the caller's
    /// resource policy and projected into the document, so this is the line that
    /// decides whether a PDF's photographs are visible in the Writer or reported
    /// as skipped.
    /// </para>
    /// <para>
    /// <strong>Why JPEG and nothing else.</strong> Not because the others are
    /// uncleared — IP-008 approved JBIG2 and IP-009 retired the fax patent
    /// position — but because both of their decode paths rest on
    /// <c>SRC-017</c>, which is pending: the fax decoder needs T.4's transcribed
    /// code tables and JBIG2's MMR regions decode through that same decoder. A
    /// pending row still blocks, so neither is composed into anything that
    /// ships. JPEG 2000 has no entropy decoder to compose at all.
    /// </para>
    /// <para>
    /// Referencing <c>Broiler.Documents.Pdf.Images</c> links those adapters even
    /// so. Linking is not composing, and the composition test asserts the
    /// difference.
    /// </para>
    /// </remarks>
    private static PdfCodecServices CreatePdfServices() =>
        PdfCodecServices.Base.WithStreamFilters(new JpegStreamFilter());


    private const uint MbOk = 0x00000000;
    private const uint MbIconError = 0x00000010;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hwnd, string text, string caption, uint type);
}
