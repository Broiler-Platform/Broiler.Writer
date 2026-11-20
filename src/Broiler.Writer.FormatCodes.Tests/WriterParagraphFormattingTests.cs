using Broiler.Documents.Model;
using Broiler.Documents.Pagination;
using Broiler.Graphics.Color;
using Broiler.Graphics.Geometry;
using Broiler.Graphics.RenderList;
using Broiler.Graphics.Text;
using Broiler.UI.Button.Standard;
using Broiler.UI.Dialog.Standard;
using Broiler.UI.Edit.Standard;
using Broiler.UI.RichEdit.Standard;

namespace Broiler.Writer.FormatCodes.Tests;

public sealed class WriterParagraphFormattingTests
{
    [Theory]
    [InlineData(".docx")]
    [InlineData(".odt")]
    public void Named_Styles_Can_Be_Updated_Undone_And_Reused_After_Reopening(string extension)
    {
        using var host = Host();
        using var writer = new WriterApp(host, () => { });
        writer.Editor.SetPlainText("sample\nreuse\nunused");
        RichTextPosition second = writer.Document.PositionRightOf(writer.Document.ParagraphEnd(RichTextDocument.Start));
        At(RichTextDocument.Start);
        Run(writer, "paragraph.preset.heading-large");
        Run(writer, "paragraph.properties");
        Submit(writer, "12, 6", "", "", "#E8EFFF", "", "2");
        Named("Save style", "Report heading");

        At(second);
        Named("Apply style", "report heading");
        Assert.Equal("Report heading", writer.Document.Paragraphs[1].Style.StyleName);
        Assert.Equal(20f, writer.Document.Paragraphs[1].StyleAt(0).FontSize);
        Assert.Equal(2, writer.Document.Paragraphs[1].Style.HeadingLevel);
        Run(writer, "paragraph.properties");
        Submit(writer, "", "", "", "none", "", "");

        At(RichTextDocument.Start);
        Run(writer, "paragraph.properties");
        Submit(writer, "16, 6", "", "", "", "", "");
        Named("Save style", "Report heading");
        Assert.Equal(16, writer.Document.Paragraphs[1].Style.SpacingBefore);
        Assert.True(writer.Document.Paragraphs[1].Style.Background.IsEmpty);
        Run(writer, "edit.undo");
        Assert.Equal(12, writer.Document.FindNamedStyle("Report heading")!.Paragraph.SpacingBefore);
        Run(writer, "edit.redo");

        At(writer.Document.End);
        Named("Save style", "Unused");
        Named("Detach style", "");
        using var stream = new MemoryStream();
        Assert.True(writer.WriteDocument(stream, "named" + extension));
        stream.Position = 0;
        Assert.True(writer.LoadDocument(stream, "named" + extension));
        Assert.Equal("sample\nreuse\nunused", writer.Document.PlainText);
        Assert.NotNull(writer.Document.FindNamedStyle("Unused"));
        Assert.Equal("Report heading", writer.Document.Paragraphs[1].Style.StyleName);
        Assert.True(writer.Document.Paragraphs[1].Style.Background.IsEmpty);
        At(writer.Document.End);
        Named("Apply style", "Report heading");
        Assert.Equal(16, writer.Document.Paragraphs[2].Style.SpacingBefore);
        Assert.Equal(20f, writer.Document.Paragraphs[2].StyleAt(0).FontSize);
        Assert.Equal(2, writer.Document.Paragraphs[2].Style.HeadingLevel);
        Assert.True(writer.Editor.ExecuteCommand(Broiler.UI.RichEdit.RichEditCommand.InsertText, "\n"));
        Assert.True(writer.Editor.ExecuteCommand(Broiler.UI.RichEdit.RichEditCommand.InsertText, "new text"));
        Assert.Equal(20f, writer.Document.Paragraphs[3].StyleAt(0).FontSize);

        void At(RichTextPosition caret) => writer.Editor.Selection = RichTextRange.Caret(caret);
        void Named(string button, string name)
        {
            Run(writer, "paragraph.named-styles");
            var dialog = Assert.IsType<StandardDialog>(writer.Session.ModalElement);
            var content = Assert.Single(dialog.Children);
            Assert.Single(content.Children.OfType<StandardEdit>()).Text = name;
            Assert.Single(content.Children.OfType<StandardButton>(), item => item.Text == button).Click();
            Assert.Null(writer.Session.ModalElement);
        }
    }

    [Fact]
    public void Copy_And_Paste_Keep_Referenced_Styles_And_Resolve_Conflicts()
    {
        var destinationStyle = new NamedParagraphStyle("Report", ParagraphStyle.Default with { SpacingBefore = 9 }, InlineStyle.Default);
        var sourceStyle = new NamedParagraphStyle("Report", ParagraphStyle.Default with { SpacingBefore = 24 }, InlineStyle.Default);
        RichTextDocument source = RichTextDocument.FromPlainText("body\nheading");
        source = source.ApplyParagraphStyle(RichTextRange.Caret(source.End),
            new ParagraphStyleDelta { DefineStyle = sourceStyle, SetStyleName = true, StyleName = sourceStyle.Name });
        RichTextDocument slice = source.Slice(new RichTextRange(RichTextDocument.Start, source.End));
        Assert.Equal(sourceStyle, Assert.Single(slice.StyleDefaults.NamedParagraphStyles));
        RichTextDocument target = RichTextDocument.FromPlainText("target").WithStyleDefaults(
            DocumentStyleDefaults.Default with { NamedParagraphStyles = [destinationStyle] });
        RichTextDocument pasted = target.InsertDocument(target.End, slice).Document;
        Assert.Equal(destinationStyle, pasted.FindNamedStyle("Report"));
        Assert.Equal("Report (2)", pasted.Paragraphs[1].Style.StyleName);
        Assert.Equal(24, pasted.FindNamedStyle("Report (2)")!.Paragraph.SpacingBefore);
    }

    [Fact]
    public void Live_And_Print_Keep_The_Same_List_Markers_Across_Prose()
    {
        using var host = Host();
        using var writer = new WriterApp(host, () => { });
        var primary = new ListDefinition([new ListLevel { Template = "%1." }, new ListLevel { Template = "%1.%2." }]);
        var separate = new ListDefinition([new ListLevel { Format = ListNumberFormat.UpperRoman }]);
        RichTextParagraph Item(string text, ListDefinition definition, int level, int? restart = null) =>
            RichTextParagraph.Plain(text).WithParagraphStyle(ParagraphStyle.Default with
            {
                ListKind = ListKind.Numbered, IndentLevel = level + 1, List = new ListReference(definition, level, restart),
            });
        writer.Editor.Document = RichTextDocument.FromParagraphs([
            Item("First", primary, 0, 3), Item("Nested", primary, 1), RichTextParagraph.Plain("Prose"),
            Item("Other", separate, 0), Item("Continue", primary, 0),
        ]);
        string[] expected = ["3.", "3.1.", "I.", "4."];
        var screen = WriterRenderCoordinates.ScreenText(writer.RenderFrame()).Select(command => command.Text.Text).Where(expected.Contains);
        using var print = new DocumentPaginator().Paginate(writer.Document, new DocumentPageBox(720, 720, 48, 48, 48, 48),
            TestContext.Current.CancellationToken);
        var printed = print.GetPage(0).Commands.OfType<BRenderCommand.DrawText>().Select(command => command.Text.Text).Where(expected.Contains);
        Assert.Equal(expected, screen);
        Assert.Equal(expected, printed);
    }

    [Theory]
    [InlineData(".docx")]
    [InlineData(".odt")]
    public void Paragraph_Controls_Round_Trip_Semantics_Independently_Of_A_Preset(string extension)
    {
        using var host = Host();
        using var writer = new WriterApp(host, () => { });
        writer.Editor.SetPlainText("Results\t14.2");
        Run(writer, "paragraph.preset.heading-large");
        Assert.Null(writer.Document.Paragraphs[0].Style.HeadingLevel);
        Assert.Equal(20, writer.Document.Paragraphs[0].StyleAt(0).FontSize);
        Assert.True(writer.Document.Paragraphs[0].MarkStyle.Bold);
        Run(writer, "paragraph.properties");
        Submit(writer, "6, 9", "exact 24", "12, 6, 5", "#E8EFFF", "1 #223344", "2");
        Run(writer, "paragraph.tabs");
        Submit(writer, "120 decimal dot");
        Run(writer, "edit.undo");
        Assert.Null(writer.Document.Paragraphs[0].Style.TabStops);
        Run(writer, "edit.redo");

        using var stream = new MemoryStream();
        Assert.True(writer.WriteDocument(stream, "paragraph" + extension));
        stream.Position = 0;
        Assert.True(writer.LoadDocument(stream, "paragraph" + extension));
        RichTextParagraph paragraph = writer.Document.Paragraphs[0];
        Assert.Equal("Results\t14.2", paragraph.Text);
        Assert.Equal(2, paragraph.Style.HeadingLevel);
        Assert.Equal(LineHeightRule.Exact, paragraph.Style.LineHeightRule);
        Assert.Equal(24, paragraph.Style.LineHeight);
        Assert.Equal(6, paragraph.Style.SpacingBefore);
        Assert.Equal(9, paragraph.Style.SpacingAfter);
        Assert.Equal(12, paragraph.Style.LeftIndent);
        Assert.Equal(6, paragraph.Style.RightIndent);
        Assert.Equal(5, paragraph.Style.FirstLineIndent);
        Assert.Equal(new BColor(232, 239, 255), paragraph.Style.Background);
        Assert.True(paragraph.Style.Border?.Edges.Top.IsVisible);
        ParagraphTabStop tab = Assert.Single(paragraph.Style.TabStops!);
        Assert.Equal(120, tab.Position, 2);
        Assert.Equal(TabAlignment.Decimal, tab.Alignment);
        Assert.Equal(".", tab.Leader);
    }

    [Theory]
    [InlineData(".docx")]
    [InlineData(".odt")]
    public void List_Commands_Restart_One_Item_And_Continue_Across_Prose(string extension)
    {
        using var host = Host();
        using var writer = new WriterApp(host, () => { });
        writer.Editor.SetPlainText("first\nsecond\nprose\nthird");
        RichTextDocument document = writer.Document;
        RichTextPosition second = document.PositionRightOf(document.ParagraphEnd(RichTextDocument.Start));
        writer.Editor.Selection = new RichTextRange(RichTextDocument.Start, document.ParagraphEnd(second));
        Run(writer, "paragraph.numbering");
        Submit(writer, "new", "upperRoman", "1", "", "10");
        writer.Editor.Selection = RichTextRange.Caret(second);
        Run(writer, "paragraph.numbering");
        Submit(writer, "restart", "", "1", "5", "");
        writer.Editor.Selection = RichTextRange.Caret(writer.Document.End);
        Run(writer, "paragraph.numbering");
        Submit(writer, "continue", "", "1", "", "");
        var numberer = new ListNumberer();
        Assert.Equal(["I.", "V.", null, "VI."], writer.Document.Paragraphs.Select(item => numberer.Advance(item.Style)));
        Assert.Same(writer.Document.Paragraphs[0].Style.List?.Definition, writer.Document.Paragraphs[3].Style.List?.Definition);
        Run(writer, "edit.undo");
        Assert.Null(writer.Document.Paragraphs[3].Style.List);
        Run(writer, "edit.redo");
        using var stream = new MemoryStream();
        Assert.True(writer.WriteDocument(stream, "list" + extension));
        stream.Position = 0;
        Assert.True(writer.LoadDocument(stream, "list" + extension));
        numberer = new ListNumberer();
        Assert.Equal(["I.", "V.", null, "VI."], writer.Document.Paragraphs.Select(item => numberer.Advance(item.Style)));
    }

    [Fact]
    public void Markdown_Save_Notes_Report_That_Paragraph_Borders_Are_Lost()
    {
        using var host = Host();
        using var writer = new WriterApp(host, () => { });
        writer.Editor.SetPlainText("Bordered");
        Run(writer, "paragraph.properties");
        Submit(writer, "", "", "", "", "1 #223344", "");
        using var output = new MemoryStream();
        Assert.True(writer.WriteDocument(output, "paragraph.md"));
        Run(writer, "help.save-notes");
        var notes = Assert.IsType<StandardDialog>(writer.Session.ModalElement);
        var text = Assert.Single(Assert.Single(notes.Children).Children.OfType<StandardRichEdit>());
        Assert.Contains("changes border in paragraph 1", text.Document.PlainText);
    }

    [Fact]
    public void Recovery_Uses_DOCX_When_RTF_Would_Drop_Paragraph_Tabs_And_Outline()
    {
        using var host = Host();
        using var writer = new WriterApp(host, () => { });
        writer.Editor.SetPlainText("Results\t3.2");
        Run(writer, "paragraph.properties");
        Submit(writer, "", "", "", "", "", "2");
        Run(writer, "paragraph.tabs");
        Submit(writer, "72 decimal dot");
        var options = new Broiler.Documents.DocumentReadOptions();
        var checkpoint = WriterRecoveryCheckpoint.Capture(writer.Document, new Broiler.Documents.DocumentWriteOptions(), options);
        Assert.Equal(WriterRecoveryCheckpoint.DocxV1, checkpoint.Format);
        RichTextDocument restored = WriterRecoveryCheckpoint.Read(checkpoint.Format, checkpoint.Bytes, options).Document;
        Assert.Null(WriterParagraphFidelity.DescribeFirstChange(writer.Document, restored));
    }

    [Theory]
    [InlineData("Arial", 100, 24, TabAlignment.Left)]
    [InlineData("Arial", 75, 42, TabAlignment.Center)]
    [InlineData("Courier New", 125, 32, TabAlignment.Right)]
    [InlineData("Courier New", 150, 56, TabAlignment.Decimal)]
    public void Live_And_Print_Use_The_Same_Exact_Line_Height_And_Tab_Offset(string family, int zoom, double margin, TabAlignment alignment)
    {
        using var host = Host();
        using var writer = new WriterApp(host, () => { });
        var style = ParagraphStyle.Default with
        {
            LineHeightRule = LineHeightRule.Exact, LineHeight = 24,
            TabStops = new ParagraphTabStops([new ParagraphTabStop(72, alignment)]),
        };
        var inline = InlineStyle.Default with { FontFamily = family, FontSize = 12 };
        writer.Editor.Document = RichTextDocument.FromParagraphs([
            RichTextParagraph.Create("Before\t12.3\u2028Next", inline, style),
        ]);
        Run(writer, $"view.zoom.{zoom}");
        var screen = WriterRenderCoordinates.ScreenText(writer.RenderFrame())
            .Where(command => command.Text.Font.FamilyName == family).ToArray();
        using var print = new DocumentPaginator().Paginate(writer.Document, new DocumentPageBox(720, 720, margin, margin, margin, margin),
            TestContext.Current.CancellationToken);
        var page = print.GetPage(0).Commands.OfType<BRenderCommand.DrawText>().ToArray();

        BPoint At(BRenderCommand.DrawText[] commands, string value) => Assert.Single(commands, command => command.Text.Text == value).Origin;
        double printTabOffset = At(page, "12.3").X - At(page, "Before").X;
        double screenTabOffset = BFontStyle.PixelsToPoints(At(screen, "12.3").X - At(screen, "Before").X) * 100 / zoom;
        Assert.InRange(Math.Abs(printTabOffset - screenTabOffset), 0, 0.25);
        Assert.Equal(24, At(page, "Next").Y - At(page, "Before").Y, 4);
        Assert.Equal(24, BFontStyle.PixelsToPoints(At(screen, "Next").Y - At(screen, "Before").Y) * 100 / zoom, 4);
    }

    private static WriterUiHost Host() => new(() => new BSize(1200, 800), () => 1, () => { }, _ => { });
    private static void Run(WriterApp writer, string command) => Assert.True(writer.Menu.CommandDispatcher!.TryExecute(command));
    private static void Submit(WriterApp writer, params string[] values)
    {
        var dialog = Assert.IsType<StandardDialog>(writer.Session.ModalElement);
        var content = Assert.Single(dialog.Children);
        StandardEdit[] fields = content.Children.OfType<StandardEdit>().ToArray();
        Assert.Equal(fields.Length, values.Length);
        for (int i = 0; i < values.Length; i++)
            fields[i].Text = values[i];
        Assert.Single(content.Children.OfType<StandardButton>(), button => button.Text == "Apply").Click();
        Assert.Null(writer.Session.ModalElement);
    }
}
