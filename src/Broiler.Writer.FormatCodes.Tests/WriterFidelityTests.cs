using Broiler.Documents;
using Broiler.Documents.Model;
using Broiler.Graphics.Geometry;
using Broiler.UI.RichEdit;

namespace Broiler.Writer.FormatCodes.Tests;

public sealed class WriterFidelityTests
{
    [Fact]
    public void An_Independently_Authored_Docx_Page_Break_Survives_Editing_And_Save()
    {
        using var host = new WriterUiHost(() => new BSize(1200, 800), () => 1, () => { }, _ => { });
        using var app = new WriterApp(host, () => { });
        using Stream input = typeof(WriterFidelityTests).Assembly.GetManifestResourceStream("WriterFixtures.M0Recovery.docx")!;
        Assert.True(app.LoadDocument(input, "page-break.docx"));
        Assert.Equal("DOCX recovery text", app.Document.PlainText);
        Assert.True(app.Document.Paragraphs[0].Style.PageBreakBefore);
        Assert.DoesNotContain(WriterViewDiagnostics.Collect(app.Document), note => note.Code == "writer.view.pagination");

        app.Editor.Selection = RichTextRange.Caret(app.Document.End);
        Assert.True(app.Editor.ExecuteCommand(RichEditCommand.InsertParagraphBreak));
        Assert.True(app.Editor.ExecuteCommand(RichEditCommand.InsertText, "Unrelated edit"));

        using var output = new MemoryStream();
        Assert.True(app.WriteDocument(output, "page-break.docx"));
        output.Position = 0;
        Assert.True(app.LoadDocument(output, "page-break.docx"));
        Assert.True(app.Document.Paragraphs[0].Style.PageBreakBefore);
        Assert.Equal("Unrelated edit", app.Document.Paragraphs[^1].Text);
    }

    [Theory]
    [InlineData(".rtf")]
    [InlineData(".docx")]
    [InlineData(".odt")]
    [InlineData(".html")]
    public void Underline_And_Emphasis_Survive_An_Unrelated_Edit_And_Save(string extension)
    {
        var styled = InlineStyle.Default with { Bold = true, Underline = true };
        var document = RichTextDocument.FromParagraphs([
            RichTextParagraph.Create("Keep this formatting", styled),
            RichTextParagraph.Plain("Edit here"),
        ]);

        AssertPreserved(extension, document, snapshot =>
        {
            RichTextParagraph paragraph = snapshot.Paragraphs[0];
            Assert.Equal("Keep this formatting", paragraph.Text);
            Assert.All(paragraph.Runs, run =>
            {
                Assert.True(run.Style.Bold);
                Assert.True(run.Style.Underline);
            });
        });
    }

    [Theory]
    [InlineData(".docx")]
    [InlineData(".odt")]
    public void Table_Cell_Ranges_Survive_An_Edit_After_The_Table(string extension)
    {
        var table = new DocumentTable(0, 2, [
            new TableRow([new TableCell(0, 1, 0), new TableCell(1, 1, 1)]),
        ], columnWidths: [80, 100]);
        var document = RichTextDocument.FromPlainText("Left cell\nRight cell\nEdit here").WithTables([table]);

        AssertPreserved(extension, document, snapshot =>
        {
            DocumentTable actual = Assert.Single(snapshot.Tables);
            Assert.Equal(0, actual.ParagraphIndex);
            Assert.Equal(2, actual.ParagraphCount);
            Assert.Collection(Assert.Single(actual.Rows).Cells,
                cell => AssertCell(cell, 0, "Left cell"),
                cell => AssertCell(cell, 1, "Right cell"));

            void AssertCell(TableCell cell, int index, string text)
            {
                Assert.Equal(index, cell.ColumnIndex);
                Assert.Equal(index, cell.ParagraphIndex);
                Assert.Equal(1, cell.ParagraphCount);
                Assert.Equal(text, snapshot.Paragraphs[cell.ParagraphIndex].Text);
            }
        });
    }

    private static void AssertPreserved(string extension, RichTextDocument document, Action<RichTextDocument> assertFeature)
    {
        using var host = new WriterUiHost(() => new BSize(1200, 800), () => 1, () => { }, _ => { });
        using var app = new WriterApp(host, () => { });
        string name = "fidelity" + extension;
        DocumentCodec codec = app.DocumentFormats.FindForSave(extension)!.Codec;

        using var input = new MemoryStream();
        Assert.Equal(DocumentResultStatus.Success, codec.Write(document, input).Status);
        input.Position = 0;
        DocumentReadResult codecBaseline = codec.Read(input);
        Assert.Equal(DocumentResultStatus.Success, codecBaseline.Status);
        assertFeature(codecBaseline.Document);

        input.Position = 0;
        Assert.True(app.LoadDocument(input, name));
        assertFeature(app.Document);
        app.Editor.Selection = RichTextRange.Caret(app.Document.End);
        Assert.True(app.Editor.ExecuteCommand(RichEditCommand.InsertText, "!"));
        Assert.Equal("Edit here!", app.Document.Paragraphs[^1].Text);
        assertFeature(app.Document);
        Assert.True(app.Editor.ExecuteCommand(RichEditCommand.Undo));
        Assert.Equal("Edit here", app.Document.Paragraphs[^1].Text);
        assertFeature(app.Document);
        Assert.True(app.Editor.ExecuteCommand(RichEditCommand.Redo));
        Assert.Equal("Edit here!", app.Document.Paragraphs[^1].Text);
        assertFeature(app.Document);

        using var output = new MemoryStream();
        Assert.True(app.WriteDocument(output, name));
        output.Position = 0;
        Assert.True(app.LoadDocument(output, name));
        Assert.Equal("Edit here!", app.Document.Paragraphs[^1].Text);
        assertFeature(app.Document);
    }
}
