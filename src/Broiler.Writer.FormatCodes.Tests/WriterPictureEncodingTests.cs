using Broiler.Documents;
using Broiler.Documents.Model;
using Broiler.Documents.Resources;
using Broiler.Graphics.Imaging;

namespace Broiler.Writer.FormatCodes.Tests;

/// <summary>
/// Covers the step that gives a picture held as decoded samples - what an opened
/// PDF produces - the encoded bytes every save format needs.
/// </summary>
/// <remarks>
/// The assertions decode what was encoded and compare samples, because the
/// promise is losslessness: a picture that arrived in the file but changed on
/// the way would pass a test that only counted pictures.
/// </remarks>
public sealed class WriterPictureEncodingTests
{
    // Opaque red and a half-transparent blue: the second is what a masked logo
    // is made of, and straight alpha is what has to survive.
    private static readonly byte[] Samples = [255, 0, 0, 255, 0, 64, 255, 128];

    [Fact(Timeout = 600000)]
    public void A_Decoded_Picture_Is_Encoded_As_Png_Without_Losing_A_Sample()
    {
        (RichTextDocument document, DocumentConversionContextBuilder resources) = DocumentWith(DocumentResourcePolicy.AllowOwnDocuments);

        RichTextDocument encoded = new WriterPictureEncoding().EncodeDecodedPictures(document, resources, out int count);

        Assert.Equal(1, count);
        InlineImage picture = Assert.Single(ImagesIn(encoded));
        Assert.True(picture.TryGetEncoded(out ReadOnlyMemory<byte> png, out string? contentType));
        Assert.Equal("image/png", contentType);

        using BBitmap decoded = BBitmap.Decode(png.Span);
        Assert.Equal(Samples, decoded.ToPixelBuffer().Rgba);

        // What the picture was drawn at and called, and the text around it,
        // are the picture's own and stay.
        Assert.Equal(24.0, picture.Width);
        Assert.Equal(12.0, picture.Height);
        Assert.Equal("logo", picture.AltText);
        Assert.Equal(document.PlainText, encoded.PlainText);
        Assert.True(encoded.Paragraphs[0].StyleAt(0).Bold);

        // Admitted in its own right, which is what lets the writer take its bytes.
        Assert.True(resources.Build().IsAllowed(picture.ResourceId, DocumentResourceOperations.ByteTransfer, picture.Resource));
    }

    [Fact(Timeout = 600000)]
    public void A_Picture_Saved_Twice_Is_Encoded_Once()
    {
        (RichTextDocument document, DocumentConversionContextBuilder resources) = DocumentWith(DocumentResourcePolicy.AllowOwnDocuments, twice: true);
        var encoding = new WriterPictureEncoding();

        RichTextDocument first = encoding.EncodeDecodedPictures(document, resources, out int count);
        RichTextDocument second = encoding.EncodeDecodedPictures(document, resources, out _);

        // One picture used twice is one encoding, and a second save reuses it:
        // the same bytes, and one entry for them beside the original's.
        Assert.Equal(1, count);
        List<InlineImage> pictures = [.. ImagesIn(first), .. ImagesIn(second)];
        Assert.Equal(4, pictures.Count);
        Assert.All(pictures, picture => Assert.Same(pictures[0].Resource, picture.Resource));
        Assert.Equal(2, resources.Build().Entries.Count);
    }

    [Fact(Timeout = 600000)]
    public void Without_Leave_To_Transform_The_Picture_Is_Left_For_The_Writer_To_Report()
    {
        // The read default puts a picture in the model and grants nothing that
        // takes it out again. Re-encoding is a transformation, so the picture
        // stays as it was, and the writer says why it was not written.
        (RichTextDocument document, DocumentConversionContextBuilder resources) = DocumentWith(DocumentResourcePolicy.Default);

        RichTextDocument encoded = new WriterPictureEncoding().EncodeDecodedPictures(document, resources, out int count);

        Assert.Equal(0, count);
        Assert.Same(document, encoded);
        Assert.Single(resources.Build().Entries);
    }

    [Fact(Timeout = 600000)]
    public void A_Picture_That_Already_Has_Bytes_Is_Left_As_It_Is()
    {
        var resources = new DocumentConversionContextBuilder(DocumentResourcePolicy.AllowOwnDocuments);
        InlineImage png = resources.AdmitImage(
            new InlineImage(BImageResource.FromEncoded(new byte[] { 1, 2, 3 }, "image/png", 1, 1)),
            DocumentResourceProvenance.CallerSupplied,
            DocumentResourceDisposition.Embedded);
        RichTextDocument document = RichTextDocument.FromParagraphs(
            [RichTextParagraph.Create(InlineImage.PlaceholderText, InlineStyle.Default with { Image = png })]);

        RichTextDocument encoded = new WriterPictureEncoding().EncodeDecodedPictures(document, resources, out int count);

        Assert.Equal(0, count);
        Assert.Same(document, encoded);
    }

    /// <summary>
    /// "Logo: " in bold, then a decoded two-pixel picture admitted under
    /// <paramref name="policy"/> as an opened PDF's would be.
    /// </summary>
    private static (RichTextDocument Document, DocumentConversionContextBuilder Resources) DocumentWith(
        DocumentResourcePolicy policy,
        bool twice = false)
    {
        var resources = new DocumentConversionContextBuilder(policy);
        InlineImage picture = resources.AdmitImage(
            new InlineImage(
                BImageResource.FromPixels(new BPixelBuffer(2, 1, (byte[])Samples.Clone())),
                width: 24,
                height: 12,
                altText: "logo"),
            DocumentResourceProvenance.ReadFromSource,
            DocumentResourceDisposition.Embedded);

        RichTextParagraph paragraph = RichTextParagraph.Create("Logo: ", InlineStyle.Default with { Bold = true });
        paragraph = paragraph.InsertText(paragraph.Length, InlineImage.PlaceholderText, InlineStyle.Default with { Image = picture });
        if (twice)
        {
            paragraph = paragraph.InsertText(paragraph.Length, " and ", InlineStyle.Default);
            paragraph = paragraph.InsertText(paragraph.Length, InlineImage.PlaceholderText, InlineStyle.Default with { Image = picture });
        }

        return (RichTextDocument.FromParagraphs([paragraph]), resources);
    }

    private static List<InlineImage> ImagesIn(RichTextDocument document) =>
        [.. document.Paragraphs
            .SelectMany(paragraph => paragraph.Runs)
            .Select(run => run.Style.Image)
            .OfType<InlineImage>()];
}
