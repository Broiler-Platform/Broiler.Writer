using System.IO.Compression;
using System.Xml.Linq;
using Broiler.Documents;
using Broiler.Documents.Docx;
using Broiler.Documents.Model;
using Broiler.Documents.Odt;
using Broiler.Documents.Resources;
using Broiler.Documents.Rtf;
using Broiler.Graphics.Color;
using Broiler.Graphics.Geometry;
using Broiler.Graphics.Imaging;
using Broiler.UI.Dialog.Standard;
using Broiler.UI.FileDialog.Standard;

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
    [Fact]
    public void Candidate_Model_Mapping_Preserves_Metadata_During_Actual_Png_Conversion()
    {
        var (body, resources) = DocumentWith(DocumentResourcePolicy.AllowOwnDocuments);
        InlineImage original = ImageIn(body.Paragraphs[0]);
        (double X, double Y)[] polygon = [(0, 0), (1, 0), (1, 1)];
        var shape = new DocumentShape(0, 5, 6, 20, 20, image: original)
        {
            WrapMargins = new TableCellPadding(1, 2, 3, 4),
            WrapPolygon = polygon,
        };
        var table = new DocumentTable(0, 1, [new TableRow([new TableCell(0, 1, 0)])]);
        var running = RunningContent.Empty.WithHeader(PageSelection.Default, body.Paragraphs, [shape], [table]);
        var section = new DocumentSection(0, 1, 2, 10, runningContent: running)
        {
            PageNumberFormat = ListNumberFormat.UpperRoman,
        };
        var note = new DocumentNote(NoteKind.Footnote, body.Paragraphs) { Tables = [table] };
        var document = RichTextDocument.FromParagraphs([
            RichTextParagraph.Create(DocumentNote.PlaceholderText, InlineStyle.Default with { Note = note }),
        ]).WithRunningContent(running).WithSections([section]);

        RichTextDocument saved = new WriterPictureEncoding().EncodeDecodedPictures(document, resources, out int count);

        Assert.Equal(1, count);
        DocumentShape mapped = Assert.Single(saved.RunningContent.HeaderShapes(PageSelection.Default));
        Assert.Equal(shape.WrapMargins, mapped.WrapMargins);
        Assert.Same(polygon, mapped.WrapPolygon);
        Assert.Equal(section.PageNumberFormat, Assert.Single(saved.Sections).PageNumberFormat);
        Assert.Same(table, Assert.Single(saved.RunningContent.EffectiveHeaderTables(PageSelection.Default)));
        DocumentNote savedNote = Assert.Single(saved.Notes);
        Assert.Same(table, Assert.Single(savedNote.Tables));
        InlineImage png = Assert.IsType<InlineImage>(mapped.Image);
        Assert.Same(png, ImageIn(Assert.Single(savedNote.Paragraphs)));
        Assert.True(resources.Build().IsAllowed(png.ResourceId, DocumentResourceOperations.ByteTransfer, png.Resource));
        using BBitmap decoded = BBitmap.Decode(png.Data.Span);
        Assert.Equal(Samples, decoded.ToPixelBuffer().Rgba);
        Assert.False(original.TryGetEncoded(out _, out _));
    }

    // Opaque red and a half-transparent blue: the second is what a masked logo
    // is made of, and straight alpha is what has to survive.
    private static readonly byte[] Samples = [255, 0, 0, 255, 0, 64, 255, 128];

    [Fact]
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

    [Fact]
    public void Encoding_A_Decoded_Picture_Preserves_Document_Metadata()
    {
        (RichTextDocument body, DocumentConversionContextBuilder resources) = DocumentWith(DocumentResourcePolicy.AllowOwnDocuments);
        var section = new DocumentSection(0, 1, 2, 18);
        var pageBackground = ShapeFill.Solid(BColor.FromArgb(255, 240, 230, 220));
        var numbering = new NoteNumbering(FootnoteStart: 4, EndnoteStart: 7);
        RichTextDocument document = body
            .WithSections([section])
            .WithPageBackground(pageBackground)
            .WithNoteNumbering(numbering)
            .WithShowsRevisions(false);
        InlineImage originalPicture = Assert.Single(ImagesIn(document));

        RichTextDocument encoded = new WriterPictureEncoding().EncodeDecodedPictures(document, resources, out int count);

        Assert.Equal(1, count);
        Assert.Same(section, Assert.Single(encoded.Sections));
        Assert.Same(pageBackground, encoded.PageBackground);
        Assert.Same(numbering, encoded.NoteNumbering);
        Assert.False(encoded.ShowsRevisions);

        InlineImage png = Assert.Single(ImagesIn(encoded));
        Assert.True(png.TryGetEncoded(out _, out _));
        Assert.True(resources.Build().IsAllowed(png.ResourceId, DocumentResourceOperations.ByteTransfer, png.Resource));
        Assert.Same(body.Paragraphs[0], document.Paragraphs[0]);
        Assert.Same(originalPicture, Assert.Single(ImagesIn(document)));
        Assert.False(originalPicture.TryGetEncoded(out _, out _));
    }

    [Fact]
    public void Encodes_Shared_Header_Footer_And_Note_Pictures_For_Rtf()
    {
        var (body, resources) = DocumentWith(DocumentResourcePolicy.AllowOwnDocuments);
        RichTextParagraph pictureParagraph = body.Paragraphs[0];
        var note = new DocumentNote(NoteKind.Footnote, [pictureParagraph], "*");
        var running = RunningContent.Empty
            .WithHeader(PageSelection.Default, [pictureParagraph])
            .WithFooter(PageSelection.First, [pictureParagraph])
            .WithDifferentFirstPage(true);
        var document = RichTextDocument.FromParagraphs([
            RichTextParagraph.Create(DocumentNote.PlaceholderText, InlineStyle.Default with { Note = note }),
        ]).WithRunningContent(running);

        RichTextDocument encoded = new WriterPictureEncoding().EncodeDecodedPictures(document, resources, out int count);

        Assert.Equal(1, count);
        Assert.True(encoded.RunningContent.DifferentFirstPage);
        Assert.Empty(encoded.RunningContent.Header(PageSelection.First));
        InlineImage header = ImageIn(Assert.Single(encoded.RunningContent.Header(PageSelection.Default)));
        Assert.Same(header, ImageIn(Assert.Single(encoded.RunningContent.Footer(PageSelection.First))));
        Assert.Equal("*", Assert.Single(encoded.Notes).CustomMark);
        Assert.Same(header, ImageIn(Assert.Single(Assert.Single(encoded.Notes).Paragraphs)));
        Assert.Same(note, Assert.Single(document.Notes));
        Assert.Same(running, document.RunningContent);
        Assert.False(ImageIn(pictureParagraph).TryGetEncoded(out _, out _));

        using var stream = new MemoryStream();
        RtfWriter.Write(encoded, stream, new DocumentWriteOptions(resources: resources.Build()));
        string rtf = System.Text.Encoding.ASCII.GetString(stream.ToArray());
        Assert.Equal(3, rtf.Split("\\pngblip", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Encoded_Header_Picture_Survives_An_Odt_Save_And_Reopen()
    {
        var (body, resources) = DocumentWith(DocumentResourcePolicy.AllowOwnDocuments);
        RichTextDocument document = RichTextDocument.FromPlainText("Body").WithRunningContent(
            RunningContent.Empty.WithHeader(PageSelection.Default, body.Paragraphs));
        RichTextDocument encoded = new WriterPictureEncoding().EncodeDecodedPictures(document, resources, out _);
        using var stream = new MemoryStream();
        var codec = new OdtDocumentCodec();
        codec.Write(encoded, stream, new DocumentWriteOptions(resources: resources.Build()));
        stream.Position = 0;

        var read = codec.Read(stream, new DocumentReadOptions(resourcePolicy: DocumentResourcePolicy.AllowOwnDocuments));
        InlineImage picture = ImageIn(Assert.Single(read.Document.RunningContent.Header(PageSelection.Default)));
        using BBitmap decoded = BBitmap.Decode(picture.Data.Span);
        Assert.Equal(Samples, decoded.ToPixelBuffer().Rgba);
    }

    [Fact]
    public void Writer_Reports_Missing_Docx_Header_And_Footer_Picture_Relationships()
    {
        var (body, resources) = DocumentWith(DocumentResourcePolicy.AllowOwnDocuments);
        RichTextDocument document = RichTextDocument.FromPlainText("Body").WithRunningContent(
            RunningContent.Empty.WithHeader(PageSelection.Default, body.Paragraphs)
                .WithFooter(PageSelection.Default, body.Paragraphs));
        RichTextDocument encoded = new WriterPictureEncoding().EncodeDecodedPictures(document, resources, out _);
        using var stream = new MemoryStream();

        DocumentWriteResult result = new DocxDocumentCodec().Write(encoded, stream,
            new DocumentWriteOptions(resources: resources.Build()));

        Assert.Equal(DocumentResultStatus.Success, result.Status);
        stream.Position = 0;
        using (var package = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
        {
            XNamespace drawing = "http://schemas.openxmlformats.org/drawingml/2006/main";
            XNamespace relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            foreach (string band in new[] { "header", "footer" })
            {
                ZipArchiveEntry part = Assert.Single(package.Entries, entry =>
                    entry.FullName.StartsWith("word/" + band, StringComparison.Ordinal) && entry.FullName.EndsWith(".xml", StringComparison.Ordinal));
                using (Stream xml = part.Open())
                    Assert.NotNull(Assert.Single(XDocument.Load(xml).Descendants(drawing + "blip")).Attribute(relationships + "embed"));
                package.GetEntry("word/_rels/" + part.Name + ".rels")?.Delete();
            }
        }

        DocumentWriteResult review = WriterSavePolicy.ReviewDocxNestedPictures(stream.ToArray(), result);
        Assert.Equal(DocumentResultStatus.Partial, review.Status);
        Assert.Contains(review.Diagnostics, diagnostic => diagnostic.Code == "writer.save.docx.picture-relationship");
    }

    [Theory]
    [InlineData(NoteKind.Footnote, "footnotes.xml")]
    [InlineData(NoteKind.Endnote, "endnotes.xml")]
    public void Docx_Note_Pictures_Have_Resolvable_Relationships(NoteKind kind, string partName)
    {
        var (body, resources) = DocumentWith(DocumentResourcePolicy.AllowOwnDocuments);
        var note = new DocumentNote(kind, body.Paragraphs);
        var document = RichTextDocument.FromParagraphs([
            RichTextParagraph.Create(DocumentNote.PlaceholderText, InlineStyle.Default with { Note = note }),
        ]);
        RichTextDocument encoded = new WriterPictureEncoding().EncodeDecodedPictures(document, resources, out _);
        using var stream = new MemoryStream();
        DocumentWriteResult result = new DocxDocumentCodec().Write(encoded, stream,
            new DocumentWriteOptions(resources: resources.Build()));

        stream.Position = 0;
        using var package = new ZipArchive(stream, ZipArchiveMode.Read);
        using Stream noteData = Assert.IsType<ZipArchiveEntry>(package.GetEntry("word/" + partName)).Open();
        XNamespace drawing = "http://schemas.openxmlformats.org/drawingml/2006/main";
        Assert.Single(XDocument.Load(noteData).Descendants(drawing + "blip"));
        Assert.Same(result, WriterSavePolicy.ReviewDocxNestedPictures(stream.ToArray(), result));
    }

    [Fact]
    public void Docx_Recovery_Preserves_The_Header_Picture_Or_Rejects_The_Fallback()
    {
        var (body, resources) = DocumentWith(DocumentResourcePolicy.AllowOwnDocuments);
        var document = RichTextDocument.FromParagraphs([
            RichTextParagraph.Plain(new string('x', 16384)).WithParagraphStyle(ParagraphStyle.Default with { ListKind = ListKind.Bullet }),
        ]).WithRunningContent(RunningContent.Empty.WithHeader(PageSelection.Default, body.Paragraphs));
        RichTextDocument encoded = new WriterPictureEncoding().EncodeDecodedPictures(document, resources, out _);
        var writeOptions = new DocumentWriteOptions(resources: resources.Build());
        var readOptions = new DocumentReadOptions(limits: new DocumentLimits(maxDocumentBytes: 8192),
            resourcePolicy: DocumentResourcePolicy.AllowOwnDocuments);

        if (HasDocxHeaderRelationships(encoded, writeOptions))
        {
            var checkpoint = WriterRecoveryCheckpoint.Capture(encoded, writeOptions, readOptions);
            Assert.Equal(WriterRecoveryCheckpoint.DocxV1, checkpoint.Format);
            var restored = WriterRecoveryCheckpoint.Read(checkpoint.Format, checkpoint.Bytes, readOptions).Document;
            Assert.True(ImageIn(Assert.Single(restored.RunningContent.Header(PageSelection.Default))).TryGetEncoded(out _, out _));
            return;
        }

        var error = Assert.Throws<InvalidDataException>(() =>
            WriterRecoveryCheckpoint.Capture(encoded, writeOptions, readOptions));
        Assert.Contains("missing its image relationship or media", error.Message);
    }

    [Fact]
    public void Saving_An_Odt_Header_Picture_To_Docx_Preserves_It_Or_Reviews_Before_Replacement()
    {
        var (body, resources) = DocumentWith(DocumentResourcePolicy.AllowOwnDocuments);
        RichTextDocument document = RichTextDocument.FromPlainText("Body").WithRunningContent(
            RunningContent.Empty.WithHeader(PageSelection.Default, body.Paragraphs));
        RichTextDocument encoded = new WriterPictureEncoding().EncodeDecodedPictures(document, resources, out _);
        using var stream = new MemoryStream();
        var options = new DocumentWriteOptions(resources: resources.Build());
        bool hasRelationships = HasDocxHeaderRelationships(encoded, options);
        new OdtDocumentCodec().Write(encoded, stream, options);
        stream.Position = 0;
        using var host = new WriterUiHost(() => new BSize(1200, 800), () => 1, () => { }, _ => { });
        using var app = new WriterApp(host, () => { });
        Assert.True(app.LoadDocument(stream, "source.odt"));
        ImageIn(Assert.Single(app.Document.RunningContent.Header(PageSelection.Default)));

        DirectoryInfo directory = Directory.CreateTempSubdirectory();
        try
        {
            string path = Path.Combine(directory.FullName, "existing.docx");
            File.WriteAllText(path, "previous contents");
            Assert.True(app.Menu.CommandDispatcher!.TryExecute("file.save-as"));
            Assert.IsType<StandardFileDialog>(app.Session.ModalElement).Accept(path);
            if (hasRelationships)
            {
                Assert.Null(app.Session.ModalElement);
                using FileStream written = File.OpenRead(path);
                var reopened = new DocxDocumentCodec().Read(written).Document;
                Assert.True(ImageIn(Assert.Single(reopened.RunningContent.Header(PageSelection.Default))).TryGetEncoded(out _, out _));
                return;
            }

            Assert.Equal("previous contents", File.ReadAllText(path));
            Assert.IsType<StandardDialog>(app.Session.ModalElement).Cancel();
            Assert.Equal("previous contents", File.ReadAllText(path));
            ImageIn(Assert.Single(app.Document.RunningContent.Header(PageSelection.Default)));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Encodes_Section_Running_Shapes_Text_Boxes_And_Page_Fills()
    {
        var (body, resources) = DocumentWith(DocumentResourcePolicy.AllowOwnDocuments);
        RichTextParagraph pictureParagraph = body.Paragraphs[0];
        InlineImage original = ImageIn(pictureParagraph);
        var padding = new TableCellPadding(2, 4, 6, 8);
        var shape = new DocumentShape(0, 11, 12, 30, 40, image: original, paragraphs: [pictureParagraph],
            behindText: false, wrap: ShapeWrap.Square, zOrder: 3, textPadding: padding, printable: false)
        {
            IsAsCharacter = true,
        };
        var running = RunningContent.Empty.WithHeader(PageSelection.Even, [], [shape]);
        var fill = ShapeFill.Solid(BColor.FromArgb(255, 240, 230, 220)) with { Image = new ImageFill(original) };
        var styles = new PageStyleSequence([new PageStyle(null, running, fill), new PageStyle(null, RunningContent.Empty)], 1);
        var section = new DocumentSection(0, 1, 2, 24, runningContent: running, pageStyles: styles, pageNumberStart: 4);
        RichTextDocument document = RichTextDocument.FromPlainText("Body").WithRunningContent(running)
            .WithSections([section]).WithShapes([shape]).WithPageBackground(fill);

        RichTextDocument encoded = new WriterPictureEncoding().EncodeDecodedPictures(document, resources, out int count);

        Assert.Equal(1, count);
        Assert.Same(document.Paragraphs[0], encoded.Paragraphs[0]);
        DocumentSection savedSection = Assert.Single(encoded.Sections);
        Assert.Equal(2, savedSection.ColumnCount);
        Assert.Equal(24, savedSection.ColumnGap);
        Assert.Equal(4, savedSection.PageNumberStart);
        Assert.Same(encoded.RunningContent, savedSection.RunningContent);
        PageStyleSequence savedStyles = Assert.IsType<PageStyleSequence>(savedSection.PageStyles);
        Assert.Equal(1, savedStyles.RepeatFrom);
        Assert.Same(encoded.RunningContent, savedStyles.Styles[0].RunningContent);

        DocumentShape savedShape = Assert.Single(encoded.Shapes);
        Assert.Equal(shape.OffsetX, savedShape.OffsetX);
        Assert.Equal(shape.Wrap, savedShape.Wrap);
        Assert.Equal(shape.ZOrder, savedShape.ZOrder);
        Assert.Equal(padding, savedShape.TextPadding);
        Assert.True(savedShape.IsAsCharacter);
        Assert.False(savedShape.Printable);
        InlineImage png = Assert.IsType<InlineImage>(savedShape.Image);
        Assert.Same(png, ImageIn(Assert.Single(savedShape.Paragraphs)));
        Assert.Same(png, Assert.Single(encoded.RunningContent.HeaderShapes(PageSelection.Even)).Image);
        Assert.Same(png, encoded.PageBackground!.Image!.Picture);
        Assert.Same(png, savedStyles.Styles[0].Background!.Image!.Picture);
        Assert.True(resources.Build().IsAllowed(png.ResourceId, DocumentResourceOperations.ByteTransfer, png.Resource));
        Assert.Same(original, shape.Image);
    }

    [Fact]
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Without_Leave_To_Transform_The_Picture_Is_Left_For_The_Writer_To_Report(bool inHeader)
    {
        // The read default puts a picture in the model and grants nothing that
        // takes it out again. Re-encoding is a transformation, so the picture
        // stays as it was, and the writer says why it was not written.
        (RichTextDocument document, DocumentConversionContextBuilder resources) = DocumentWith(DocumentResourcePolicy.Default);
        if (inHeader)
        {
            document = RichTextDocument.FromPlainText("Body").WithRunningContent(
                RunningContent.Empty.WithHeader(PageSelection.Default, document.Paragraphs));
        }

        RichTextDocument encoded = new WriterPictureEncoding().EncodeDecodedPictures(document, resources, out int count);

        Assert.Equal(0, count);
        Assert.Same(document, encoded);
        Assert.Single(resources.Build().Entries);
    }

    [Fact]
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

    private static InlineImage ImageIn(RichTextParagraph paragraph) =>
        Assert.Single(paragraph.Runs.Select(run => run.Style.Image).OfType<InlineImage>());

    private static bool HasDocxHeaderRelationships(RichTextDocument document, DocumentWriteOptions options)
    {
        using var stream = new MemoryStream(DocxDocumentCodec.WriteToArray(document, options));
        using var package = new ZipArchive(stream, ZipArchiveMode.Read);
        return package.GetEntry("word/_rels/header1.xml.rels") is not null;
    }
}
