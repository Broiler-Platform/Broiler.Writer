using System.Runtime.CompilerServices;
using Broiler.Documents;
using Broiler.Documents.Model;
using Broiler.Graphics;
using Broiler.Media;
using Broiler.Media.Image.Managed;

namespace Broiler.Writer.FormatCodes.Tests;

/// <summary>
/// Test-host composition root, matching what the Writer entry points do: without
/// a registered catalog the renderer cannot decode a document's images.
/// </summary>
internal static class WriterImageCodecBootstrap
{
    [ModuleInitializer]
    internal static void Register() =>
        BImageCodecs.Use(new MediaCodecCatalog(ManagedImageCodecs.CreateCodecs()));
}

/// <summary>
/// The whole chain in one place: a DOCX with a picture is opened in the Writer,
/// and the frame it renders actually contains that picture. This is what breaks
/// if any single link — codec, model, editor layout, or host image upload — is
/// wired up wrong.
/// </summary>
public sealed class WriterImageRenderTests
{
    [Fact(Timeout = 600000)]
    public void A_Picture_In_An_Opened_Docx_Reaches_The_Render_List()
    {
        using var renderer = new BImageRenderer();
        var host = new WriterUiHost(
            () => new BSize(1200, 800),
            () => 1,
            () => { },
            _ => { },
            getRenderer: () => renderer);
        using var app = new WriterApp(host, () => { });

        var image = new InlineImage(RedDotPng, "image/png", 48, 24, "a red dot");

        (image, DocumentWriteOptions writeOptions) = Writable(image);
        byte[] docx = Broiler.Documents.Docx.DocxDocumentCodec.WriteToArray(
            RichTextDocument.FromParagraphs(
            [
                RichTextParagraph.Create(InlineImage.PlaceholderText, InlineStyle.Default with { Image = image }),
            ]),
            writeOptions);

        using var stream = new MemoryStream(docx, writable: false);
        Assert.True(app.LoadDocument(stream, "picture.docx"));

        BRenderList frame = app.RenderFrame();
        BRenderCommand.DrawImage drawn = Assert.Single(
            frame.Commands.OfType<BRenderCommand.DrawImage>());

        Assert.Equal(48, drawn.Destination.Width, 3);
        Assert.Equal(24, drawn.Destination.Height, 3);
        Assert.True(drawn.Image.IsValid);
    }

    [Fact(Timeout = 600000)]
    public void The_Rendered_Frame_Actually_Contains_The_Pictures_Pixels()
    {
        using var renderer = new BImageRenderer();
        var host = new WriterUiHost(
            () => new BSize(900, 600),
            () => 1,
            () => { },
            _ => { },
            getRenderer: () => renderer);
        using var app = new WriterApp(host, () => { });

        var image = new InlineImage(RedDotPng, "image/png", 64, 64, "a red square");

        (image, DocumentWriteOptions writeOptions) = Writable(image);
        byte[] docx = Broiler.Documents.Docx.DocxDocumentCodec.WriteToArray(
            RichTextDocument.FromParagraphs(
            [
                RichTextParagraph.Create(InlineImage.PlaceholderText, InlineStyle.Default with { Image = image }),
            ]),
            writeOptions);

        using var stream = new MemoryStream(docx, writable: false);
        Assert.True(app.LoadDocument(stream, "picture.docx"));

        BRenderList frame = app.RenderFrame();
        var descriptor = BSurfaceDescriptor.Default(new BSize(900, 600));
        using BBitmap bitmap = renderer.RenderToImage(frame, descriptor, BFrameContext.Default);

        BRenderCommand.DrawImage drawn = Assert.Single(frame.Commands.OfType<BRenderCommand.DrawImage>());
        BColor sample = bitmap.GetPixel(
            (int)Math.Round(drawn.Destination.Left + (drawn.Destination.Width / 2)),
            (int)Math.Round(drawn.Destination.Top + (drawn.Destination.Height / 2)));

        // The source PNG is solid red; the middle of the destination rectangle is
        // where it lands on the page.
        Assert.True(sample.R > 200, "expected red at the picture's centre, got " + sample);
        Assert.True(sample.G < 60, "expected red at the picture's centre, got " + sample);
        Assert.True(sample.B < 60, "expected red at the picture's centre, got " + sample);
    }

    [Fact(Timeout = 600000)]
    public void A_Picture_The_Renderer_Cannot_Decode_Does_Not_Break_The_Frame()
    {
        using var renderer = new BImageRenderer();
        var host = new WriterUiHost(
            () => new BSize(1200, 800),
            () => 1,
            () => { },
            _ => { },
            getRenderer: () => renderer);
        using var app = new WriterApp(host, () => { });

        // A PNG signature over bytes that decode to nothing: the document is
        // still readable, and the editor must render rather than throw.
        byte[] brokenBytes = [.. RedDotPng[..8], 0, 0, 0, 0];
        var broken = new InlineImage(brokenBytes, "image/png", 40, 20);
        (broken, DocumentWriteOptions writeOptions) = Writable(broken);
        byte[] docx = Broiler.Documents.Docx.DocxDocumentCodec.WriteToArray(
            RichTextDocument.FromParagraphs(
            [
                RichTextParagraph.Create("text ", InlineStyle.Default)
                    .InsertText(5, InlineImage.PlaceholderText, InlineStyle.Default with { Image = broken }),
            ]));

        using var stream = new MemoryStream(docx, writable: false);
        Assert.True(app.LoadDocument(stream, "broken.docx"));

        BRenderList frame = app.RenderFrame();

        Assert.Empty(frame.Commands.OfType<BRenderCommand.DrawImage>());
        Assert.Contains(
            frame.Commands.OfType<BRenderCommand.DrawText>(),
            command => command.Text.Text.Contains("text", StringComparison.Ordinal));
    }

    /// <summary>An 8x8 red PNG — real bytes, so the managed decoder produces pixels.</summary>
    private static readonly byte[] RedDotPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAIAAABLbSncAAAAEUlEQVR4" +
        "2mP4z8CAFTEMLQkAKP8/wc53yE8AAAAASUVORK5CYII=");

    /// <summary>
    /// Admits <paramref name="image"/> under a policy that permits writing it,
    /// and returns the image plus the options a writer needs.
    /// </summary>
    /// <remarks>
    /// A fixture written without a decision comes out with no picture in it, so
    /// a test that needs a DOCX containing one has to say under which decision.
    /// </remarks>
    private static (InlineImage Image, DocumentWriteOptions Options) Writable(InlineImage image)
    {
        var builder = new DocumentConversionContextBuilder(DocumentResourcePolicy.AllowOwnDocuments);
        InlineImage admitted = builder.AdmitImage(
            image,
            DocumentResourceProvenance.CallerSupplied,
            DocumentResourceDisposition.Embedded);

        return (admitted, new DocumentWriteOptions(resources: builder.Build()));
    }

    [Fact(Timeout = 600000)]
    public void A_Picture_Masked_To_An_Ellipse_Reaches_The_Backend_With_Its_Corners_Cleared()
    {
        // The end of the chain, and the reason it is asserted here rather than in
        // Broiler.UI: shaping a picture means decoding it, decoding needs a codec
        // catalog a composition root registers, and this is where one is. In the
        // UI component the same path falls back to handing the bytes over
        // unshaped, which is correct there and proves nothing about the mask.
        using var renderer = new BImageRenderer();
        var host = new WriterUiHost(
            () => new BSize(400, 400),
            () => 1,
            () => { },
            _ => { },
            getRenderer: () => renderer);
        using var app = new WriterApp(host, () => { });

        using var source = new BBitmap(64, 64);
        source.Clear(new BColor(0x20, 0x40, 0x60, 0xFF));
        var image = new InlineImage(
            source.Encode(),
            "image/png",
            64,
            64,
            presentation: new ImagePresentation { Mask = ImageMask.Ellipse });

        // Through a real DOCX, so the presentation is asserted to survive the
        // whole path a user's document takes: written, read back, laid out, drawn.
        (image, DocumentWriteOptions writeOptions) = Writable(image);
        byte[] docx = Broiler.Documents.Docx.DocxDocumentCodec.WriteToArray(
            RichTextDocument.FromParagraphs(
            [
                RichTextParagraph.Create(
                    InlineImage.PlaceholderText, InlineStyle.Default with { Image = image }),
            ]),
            writeOptions);

        using var stream = new MemoryStream(docx, writable: false);
        Assert.True(app.LoadDocument(stream, "portrait.docx"));

        BRenderList frame = app.RenderFrame();
        var descriptor = BSurfaceDescriptor.Default(new BSize(400, 400));
        using BBitmap page = renderer.RenderToImage(frame, descriptor, BFrameContext.Default);

        BRenderCommand.DrawImage drawn = Assert.Single(
            frame.Commands.OfType<BRenderCommand.DrawImage>());

        // The picture's own corner is the page's white, and its middle is not.
        var corner = new BPoint(drawn.Destination.Left + 1, drawn.Destination.Top + 1);
        var middle = new BPoint(
            drawn.Destination.Left + (drawn.Destination.Width / 2),
            drawn.Destination.Top + (drawn.Destination.Height / 2));

        BColor cornerPixel = page.GetPixel((int)corner.X, (int)corner.Y);
        BColor middlePixel = page.GetPixel((int)middle.X, (int)middle.Y);

        Assert.True(
            cornerPixel.R > 0xE0 && cornerPixel.G > 0xE0 && cornerPixel.B > 0xE0,
            $"the picture's corner drew as {cornerPixel.R:X2}{cornerPixel.G:X2}{cornerPixel.B:X2}, not the page behind it");
        Assert.True(
            middlePixel.B > middlePixel.R,
            $"the picture's middle drew as {middlePixel.R:X2}{middlePixel.G:X2}{middlePixel.B:X2}, not the picture");
    }
}
