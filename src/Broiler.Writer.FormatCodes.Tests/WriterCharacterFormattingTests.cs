using Broiler.Documents.Model;
using Broiler.Graphics.Color;
using Broiler.Graphics.Geometry;
using Broiler.UI.Button.Standard;
using Broiler.UI.ColorDialog.Standard;
using Broiler.UI.Dialog.Standard;
using Broiler.UI.Edit.Standard;
using Broiler.UI.RichEdit;
using Broiler.UI.RichEdit.Standard;

namespace Broiler.Writer.FormatCodes.Tests;

public sealed class WriterCharacterFormattingTests
{
    [Theory]
    [InlineData(".rtf")]
    [InlineData(".docx")]
    [InlineData(".odt")]
    [InlineData(".html")]
    public void Color_Highlight_And_Link_Survive_Reopening(string extension)
    {
        using var host = CreateHost();
        using var app = new WriterApp(host, () => { });
        app.Editor.SetPlainText("Link");
        app.Editor.Selection = new(RichTextDocument.Start, app.Document.End);
        Execute(app, "format.text-color");
        ChooseColor(app, "#336699");
        Execute(app, "format.highlight");
        ChooseColor(app, "#EFC357");
        Execute(app, "format.link");
        Field(app, 1).Text = "https://example.com/a";
        Click(app, "Apply");

        string name = "link" + extension;
        using var output = new MemoryStream();
        Assert.True(app.WriteDocument(output, name));
        output.Position = 0;
        Assert.True(app.LoadDocument(output, name));
        Assert.Equal("Link", app.Document.PlainText);
        InlineStyle style = app.Document.Paragraphs[0].StyleAt(0);
        Assert.Equal(new BColor(0x33, 0x66, 0x99), style.Foreground);
        Assert.Equal(new BColor(0xEF, 0xC3, 0x57), style.Background);
        Assert.Equal("https://example.com/a", style.LinkHref);
    }

    [Fact]
    public void Color_At_Caret_Affects_Typing_And_Clear_Changes_Only_That_Property()
    {
        using var host = CreateHost();
        using var app = new WriterApp(host, () => { });
        app.Editor.SetPlainText("Before ");
        RichTextPosition caret = app.Document.End;
        app.Editor.Selection = RichTextRange.Caret(caret);
        Execute(app, "format.text-color");
        ChooseColor(app, "FF0000");
        Execute(app, "format.highlight");
        ChooseColor(app, "FFFF00");

        Assert.True(app.Editor.ExecuteCommand(RichEditCommand.InsertText, "after"));
        Assert.True(app.Document.Paragraphs[0].StyleAt(0).Foreground.IsEmpty);
        Assert.Equal(new BColor(255, 0, 0), app.Document.Paragraphs[0].StyleAt(7).Foreground);
        app.Editor.Selection = new(app.Document.End, caret);
        Execute(app, "format.text-color");
        ChooseColor(app, "#FF000000");
        Assert.True(app.Document.Paragraphs[0].StyleAt(7).Foreground.IsEmpty);
        Assert.Equal(new BColor(255, 255, 0), app.Document.Paragraphs[0].StyleAt(7).Background);
        Execute(app, "edit.undo");
        Assert.Equal(new BColor(255, 0, 0), app.Document.Paragraphs[0].StyleAt(7).Foreground);
    }

    [Fact]
    public void Mixed_Selection_And_Caret_Link_Editing_Use_Single_Undo_Units()
    {
        var first = new Uri("https://example.org");
        Uri? opened = null;
        using var host = CreateHost();
        using var app = new WriterApp(host, () => { }, openUrl: url => opened = url);
        app.Editor.Document = RichTextDocument.FromParagraphs([
            RichTextParagraph.Create("A", InlineStyle.Default with { LinkHref = first.AbsoluteUri })
                .InsertText(1, "B", InlineStyle.Default with { Bold = true })
        ]);
        app.Editor.Selection = new(app.Document.End, RichTextDocument.Start);
        Execute(app, "format.link");
        Assert.Equal("Mixed link targets", Field(app, 1).PlaceholderText);
        Field(app, 1).Text = "javascript:alert(1)";
        Click(app, "Apply");
        Assert.IsType<StandardDialog>(app.Session.ModalElement);
        Assert.Null(app.Document.Paragraphs[0].StyleAt(1).LinkHref);
        Field(app, 1).Text = first.AbsoluteUri;
        Click(app, "Apply");
        Assert.True(app.Document.Paragraphs[0].StyleAt(1).Bold);
        Assert.Equal(first.AbsoluteUri, app.Document.Paragraphs[0].StyleAt(1).LinkHref);

        app.Editor.Selection = RichTextRange.Caret(app.Document.End);
        Execute(app, "format.link");
        Assert.Equal("AB", Field(app, 0).Text);
        Click(app, "Open link");
        Assert.Equal(first, opened);
        Click(app, "Remove link");
        Assert.All(app.Document.Paragraphs[0].Runs, run => Assert.Null(run.Style.LinkHref));
        Execute(app, "edit.undo");
        Assert.All(app.Document.Paragraphs[0].Runs, run => Assert.Equal(first.AbsoluteUri, run.Style.LinkHref));
    }

    [Fact]
    public void Link_Display_Text_Can_Be_Inserted_And_Renamed()
    {
        using var host = CreateHost();
        using var app = new WriterApp(host, () => { });
        app.Editor.SetPlainText("");
        Execute(app, "format.link");
        Field(app, 0).Text = "Site";
        Field(app, 1).Text = "https://example.org/";
        Click(app, "Apply");
        Assert.Equal("Site", app.Document.PlainText);
        Assert.Equal("https://example.org/", app.Document.Paragraphs[0].StyleAt(0).LinkHref);
        Execute(app, "format.link");
        Field(app, 0).Text = "Website";
        Click(app, "Apply");
        Assert.Equal("Website", app.Document.PlainText);
        Execute(app, "edit.undo");
        Assert.Equal("Site", app.Document.PlainText);
    }

    [Fact]
    public void Applying_A_Link_To_Multiple_Paragraphs_Keeps_The_Paragraph_Break()
    {
        using var host = CreateHost();
        using var app = new WriterApp(host, () => { });
        app.Editor.SetPlainText("First\nSecond");
        app.Editor.Selection = new(app.Document.End, RichTextDocument.Start);
        Execute(app, "format.link");
        Assert.True(Field(app, 0).IsReadOnly);
        Field(app, 1).Text = "https://example.org/";
        Click(app, "Apply");
        Assert.Equal("First\nSecond", app.Document.PlainText);
        Assert.All(app.Document.Paragraphs, paragraph => Assert.Equal("https://example.org/", paragraph.StyleAt(0).LinkHref));
    }

    [Fact]
    public void Markdown_Saves_The_Link_And_Reports_The_Color_Loss()
    {
        using var host = CreateHost();
        using var app = new WriterApp(host, () => { });
        app.Editor.Document = RichTextDocument.FromParagraphs([RichTextParagraph.Create("Site", InlineStyle.Default with
        {
            LinkHref = "https://example.org/",
            Foreground = new BColor(0x33, 0x66, 0x99),
            Background = new BColor(255, 255, 0),
        })]);
        using var output = new MemoryStream();
        Assert.True(app.WriteDocument(output, "link.md"));
        Execute(app, "help.save-notes");
        var notes = Assert.IsType<StandardDialog>(app.Session.ModalElement);
        Assert.Contains("markdown.inline-style", notes.Children.Single().Children.OfType<StandardRichEdit>().Single().Document.PlainText);
        notes.Accept();
        output.Position = 0;
        Assert.True(app.LoadDocument(output, "link.md"));
        Assert.Equal("https://example.org/", app.Document.Paragraphs[0].StyleAt(0).LinkHref);
    }

    private static void Execute(WriterApp app, string command) => Assert.True(app.Menu.CommandDispatcher!.TryExecute(command));
    [Theory]
    [InlineData(".docx")]
    [InlineData(".odt")]
    public void Advanced_Formatting_At_The_Caret_Round_Trips_With_Original_Text(string extension)
    {
        string clipboard = "";
        using var host = new WriterUiHost(() => new BSize(1200, 800), () => 1, () => { }, _ => { },
            getClipboardText: () => clipboard, setClipboardText: text => clipboard = text);
        using var app = new WriterApp(host, () => { });
        app.Editor.SetPlainText("A");
        app.Editor.Selection = RichTextRange.Caret(app.Document.End);
        Execute(app, "format.superscript");
        Execute(app, "format.hidden-text");
        Execute(app, "format.language");
        Field(app, 0).Text = "tr";
        Click(app, "Apply");
        Assert.True(app.Editor.ExecuteCommand(RichEditCommand.InsertText, "2"));

        RichTextDocument styled = app.Document;
        Execute(app, "view.hidden-text");
        Assert.False(app.Editor.ShowHiddenText);
        Assert.Same(styled, app.Document);
        app.Editor.Selection = new(RichTextDocument.Start, app.Document.End);
        Assert.True(app.Editor.ExecuteCommand(RichEditCommand.Copy));
        Assert.True(host.TryGetText(out string clip));
        Assert.Equal("A2", clip);
        Execute(app, "edit.undo");
        Assert.Equal("A", app.Document.PlainText);
        Execute(app, "edit.redo");

        using var bytes = new MemoryStream();
        Assert.True(app.WriteDocument(bytes, "styles" + extension));
        bytes.Position = 0;
        Assert.True(app.LoadDocument(bytes, "styles" + extension));
        Assert.Equal("A2", app.Document.PlainText);
        InlineStyle style = app.Document.Paragraphs[0].StyleAt(1);
        Assert.True(style.Hidden);
        Assert.Equal("tr", style.Language);
        Assert.Equal(BaselinePosition.AutoSuperscript, style.Shift?.Position);

        var readOptions = new Broiler.Documents.DocumentReadOptions();
        var checkpoint = WriterRecoveryCheckpoint.Capture(styled, new Broiler.Documents.DocumentWriteOptions(), readOptions);
        Assert.Equal(WriterRecoveryCheckpoint.DocxV1, checkpoint.Format);
        InlineStyle restored = WriterRecoveryCheckpoint.Read(checkpoint.Format, checkpoint.Bytes, readOptions).Document.Paragraphs[0].StyleAt(1);
        Assert.Equal(style.Language, restored.Language);
        Assert.Equal(style.Shift, restored.Shift);
    }

    [Fact]
    public void Advanced_Format_Codes_Expose_Properties_With_Source_Ranges()
    {
        using var host = CreateHost();
        using var app = new WriterApp(host, () => { });
        app.Editor.Document = RichTextDocument.FromParagraphs([RichTextParagraph.Create("i", InlineStyle.Default with
        {
            Hidden = true,
            Language = "tr",
            Shift = BaselineShift.Subscript(),
            CharacterStyle = "Reference",
        })]);
        var source = app.Document;
        var projection = Broiler.Documents.FormatCodes.FormatCodeProjector.Project(source, cancellationToken: TestContext.Current.CancellationToken);
        var descriptor = Assert.Single(projection.Tokens,
            token => token.DisplayText == "[Hidden ON]").EditDescriptor;
        Assert.NotNull(descriptor);
        Assert.Equal(RichTextDocument.Start, descriptor.RemovalIntent.Range.Start);
        Assert.Equal(source.End, descriptor.RemovalIntent.Range.End);
        Assert.Contains(projection.Tokens, token => token.DisplayText == "[Subscript ON]");
        Assert.Contains(projection.Tokens, token => token.DisplayText == "[Language \"tr\"]");
        Assert.Contains(projection.Tokens, token => token.DisplayText == "[Character Style \"Reference\"]");
    }

    [Theory]
    [InlineData(".docx")]
    [InlineData(".rtf")]
    public void Character_Style_Identity_And_Resolved_Appearance_Reopen(string extension)
    {
        using var host = CreateHost();
        using var app = new WriterApp(host, () => { });
        app.Editor.Document = RichTextDocument.FromParagraphs([RichTextParagraph.Create("reference", InlineStyle.Default with
        {
            CharacterStyle = "Reference",
            Bold = true,
        })]);
        using var output = new MemoryStream();
        Assert.True(app.WriteDocument(output, "named-style" + extension));
        output.Position = 0;
        Assert.True(app.LoadDocument(output, "named-style" + extension));
        InlineStyle style = app.Document.Paragraphs[0].StyleAt(0);
        Assert.Equal("Reference", style.CharacterStyle);
        Assert.True(style.Bold);
    }

    private static void ChooseColor(WriterApp app, string hex)
    {
        var chooser = Assert.IsType<StandardColorDialog>(app.Session.ModalElement);
        Assert.Single(chooser.Children.OfType<StandardEdit>(), field => field.AccessibleName == "Hex color").Text = hex;
        Assert.Single(chooser.Children.OfType<StandardButton>(), button => button.Text == "OK").Click();
    }

    private static StandardEdit Field(WriterApp app, int index) =>
        Assert.IsType<StandardDialog>(app.Session.ModalElement).Children.Single().Children.OfType<StandardEdit>().ElementAt(index);
    private static void Click(WriterApp app, string text) =>
        Assert.Single(Assert.IsType<StandardDialog>(app.Session.ModalElement).Children.Single().Children.OfType<StandardButton>(),
            button => button.Text == text).Click();
    private static WriterUiHost CreateHost() => new(() => new BSize(1200, 800), () => 1, () => { }, _ => { });
}
