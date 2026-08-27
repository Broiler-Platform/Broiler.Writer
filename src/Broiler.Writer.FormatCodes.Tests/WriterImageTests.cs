using Broiler.Documents.Model;
using Broiler.Graphics;

namespace Broiler.Writer.FormatCodes.Tests;

/// <summary>
/// Inserting a picture in the Writer and saving it back out: the image reaches
/// the document as one character sized from the file's own pixels, and a
/// save-and-reopen through DOCX brings it back.
/// </summary>
public sealed class WriterImageTests
{
    /// <summary>A 4x2 PNG: only the IHDR is read, so the pixel data can be a stub.</summary>
    private static readonly byte[] FourByTwoPng =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x02,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00,
    ];

    [Fact(Timeout = 600000)]
    public void Inserts_A_Picture_As_One_Character_At_Its_Pixel_Size()
    {
        using WriterApp app = CreateApp();
        string path = WriteTempImage("insert.png", FourByTwoPng);

        try
        {
            Assert.True(app.InsertPicture(path));

            // The picture takes exactly one character position in the document.
            Assert.Equal(1, app.Document.PlainText.Count(c => c == InlineImage.Placeholder));

            InlineImage image = Assert.IsType<InlineImage>(SingleImage(app.Document));
            Assert.Equal("image/png", image.ContentType);
            Assert.Equal(4, image.Width);
            Assert.Equal(2, image.Height);

            // The file name becomes the alternative text; the temp prefix is
            // part of the name this test wrote.
            Assert.EndsWith("insert", image.AltText, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact(Timeout = 600000)]
    public void Refuses_A_File_That_Is_Not_An_Image()
    {
        using WriterApp app = CreateApp();
        string path = WriteTempImage("notes.txt", "just text"u8.ToArray());

        try
        {
            Assert.False(app.InsertPicture(path));
            Assert.DoesNotContain(InlineImage.Placeholder, app.Document.PlainText);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact(Timeout = 600000)]
    public void Round_Trips_An_Inserted_Picture_Through_Docx()
    {
        using WriterApp app = CreateApp();
        string path = WriteTempImage("logo.png", FourByTwoPng);

        try
        {
            Assert.True(app.InsertPicture(path));

            using var saved = new MemoryStream();
            Assert.True(app.WriteDocument(saved, "doc.docx", updateIdentity: false));

            using WriterApp reopened = CreateApp();
            using var source = new MemoryStream(saved.ToArray(), writable: false);
            Assert.True(reopened.LoadDocument(source, "doc.docx"));

            InlineImage image = Assert.IsType<InlineImage>(SingleImage(reopened.Document));
            Assert.Equal(FourByTwoPng, image.Data.ToArray());
            Assert.Equal("image/png", image.ContentType);
            Assert.Equal(4, image.Width, 3);
            Assert.Equal(2, image.Height, 3);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact(Timeout = 600000)]
    public void Scales_A_Wide_Picture_Down_And_Keeps_Its_Aspect_Ratio()
    {
        // 2000x1000 in the IHDR; the Writer caps an inserted picture's width.
        byte[] wide = (byte[])FourByTwoPng.Clone();
        WriteBigEndian(wide, 16, 2000);
        WriteBigEndian(wide, 20, 1000);

        BSize size = WriterImageFormats.MeasureDisplaySize(wide, maxWidth: 420);

        Assert.Equal(420, size.Width);
        Assert.Equal(210, size.Height);
    }

    [Fact(Timeout = 600000)]
    public void Reads_The_Pixel_Size_Of_A_Gif_And_A_Bmp()
    {
        byte[] gif = [.."GIF89a"u8, 0x0A, 0x00, 0x05, 0x00];
        Assert.True(WriterImageFormats.TryReadPixelSize(gif, out int gifWidth, out int gifHeight));
        Assert.Equal(10, gifWidth);
        Assert.Equal(5, gifHeight);

        byte[] bmp = new byte[26];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        bmp[18] = 8;
        bmp[22] = 0xFC; // -4 little-endian: a top-down bitmap, whose height is still four rows.
        bmp[23] = 0xFF;
        bmp[24] = 0xFF;
        bmp[25] = 0xFF;
        Assert.True(WriterImageFormats.TryReadPixelSize(bmp, out int bmpWidth, out int bmpHeight));
        Assert.Equal(8, bmpWidth);
        Assert.Equal(4, bmpHeight);
    }

    /// <summary>The one image in a document, so a test need not know which run holds it.</summary>
    private static InlineImage? SingleImage(RichTextDocument document) =>
        Assert.Single(document.Paragraphs
            .SelectMany(paragraph => paragraph.Runs)
            .Select(run => run.Style.Image)
            .Where(image => image is not null));

    private static void WriteBigEndian(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static string WriteTempImage(string fileName, byte[] bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-" + fileName);
        File.WriteAllBytes(path, bytes);
        return path;
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
