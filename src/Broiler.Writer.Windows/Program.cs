using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Broiler.Documents.Pdf;
using Broiler.Documents.Pdf.Images;
using Broiler.Documents.Pdf.Fonts;
using Broiler.Graphics.Imaging;
using Broiler.Media;
using Broiler.Media.Image.Managed;

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
        BImageCodecs.Use(
            new MediaCodecCatalog(ManagedImageCodecs.CreateCodecs()));

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
    /// The PDF service graph this head composes: the JPEG decoder, the sfnt
    /// font-program reader, and a URI policy that admits the schemes a
    /// document's links are ordinarily written in.
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
    /// <para>
    /// <strong>Why the font-program reader is composed.</strong> A PDF says what
    /// its glyphs spell in one of two ways: a <c>ToUnicode</c> map, or not at
    /// all. A subsetted symbolic font without one extracts as nothing - not as
    /// wrong text, as no text - and the program the document already embeds
    /// carries the answer in its own character map. IP-012 approved reading one
    /// for exactly that, and only that: no outlines, no metrics, no shaping, and
    /// no embedding, which this release does not do at all.
    /// </para>
    /// <para>
    /// It stays a satellite package and an explicit opt-in because a font parser
    /// is one of the two largest attack surfaces a PDF reader has, so a host that
    /// composes none links none. <c>GraphicsFontProgramReader</c> goes to
    /// <c>BFontProgramInspector</c> rather than the renderer's parser for the
    /// same reason the JPEG decoder is a separate decision: the inspector refuses
    /// a malformed program where the renderer's parser repairs it into plausible
    /// output, and on this side of the boundary the program arrived inside
    /// somebody else's document (PDF roadmap &#167;6.5).
    /// </para>
    /// <para>
    /// <strong>Why http and mailto are admitted.</strong> The policy's own
    /// default is absolute <c>https</c> alone, which is the right default for a
    /// library that cannot know what its caller will do with a link: a target a
    /// reader admits is one a writer may later be asked to emit. A Writer opening
    /// a document a person chose to open is the narrower case the default is
    /// strict for. A <c>mailto:</c> in a letterhead and an <c>http://</c> on a
    /// document older than the web's move to TLS are ordinary content, and
    /// refusing them turned real links into plain text with a line in a dialog as
    /// the only trace.
    /// </para>
    /// <para>
    /// This widens the scheme list and nothing else. Everything the policy
    /// refuses on other grounds stays refused - <c>javascript:</c>,
    /// <c>file:</c>, <c>data:</c>, local and UNC paths, protocol-handler and
    /// unknown schemes, values that are not absolute URIs, targets carrying user
    /// information or no host, and anything past the length cap - and validation
    /// still performs no I/O of any kind, so opening a document never contacts a
    /// link (ADR 0009). The deliberate half of the trade is that a plain
    /// <c>http</c> target now becomes an active link in the opened document
    /// rather than inert text.
    /// </para>
    /// </remarks>
    private static PdfCodecServices CreatePdfServices() =>
        PdfCodecServices.Base
            .WithStreamFilters(new JpegStreamFilter())
            .WithUriPolicy(new PdfUriPolicy(allowHttp: true, allowMailto: true))
            .WithFontProgramReader(new GraphicsFontProgramReader());


    private const uint MbOk = 0x00000000;
    private const uint MbIconError = 0x00000010;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hwnd, string text, string caption, uint type);
}
